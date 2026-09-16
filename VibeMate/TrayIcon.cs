using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VibeMate;

/// <summary>
/// 托盘图标（用户定稿：无窗口主程序 + 托盘 + 点击打开浏览器设置页）。
/// 左键单击 → 打开 http://127.0.0.1:&lt;port&gt;；右键 → 菜单（设置/重启/退出）。
///
/// 动效素材是嵌入资源 VibeMate.tray.icons.png（3×3 宫格，2026-09-16 用户出图并
/// 重新抠好透明底）：列=状态组（第 1 列待机 / 第 2 列语音输入 / 第 3 列普通按键），
/// 行=帧。节奏：
///   · Idle：3 帧慢循环（700ms/帧 —— 闭眼小家伙的呼吸感）；
///   · Voice：3 帧快循环（200ms/帧 —— 橙色张嘴 + 声浪）；
///   · KeyPulse（映射命中）：3 帧一次性（130ms/帧）播完回 Idle。
/// 语音态优先于按键闪动（两者并发的概率≈0：未改映射的语音键不会命中映射表）。
///
/// ★ 9 帧在启动时一次渲染并缓存成 Icon 实例 —— 动画运行期换帧只是引用赋值，
///   零 GDI 分配。素材缺失/损坏时退化为 GDI 简笔帧（保住三态语义，托盘不裸奔）。
/// ★ 刷新用 WinForms Timer（UI 线程、常开 —— 三态都靠它驱动）；外部线程的事件
///   （管道读线程/GATT 回调）经 SynchronizationContext Post 回 UI 线程再碰图标
///   —— NotifyIcon 非线程安全。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private enum AnimMode { Idle, Voice, KeyPulse }

    private const int Frames = 3;              // 每组帧数 = 宫格行数
    private const int IconSize = 32;           // 托盘帧尺寸（高 DPI 下限）
    // 帧节奏（数组下标即 (int)AnimMode）：待机慢呼吸、语音活跃、按键快闪
    private static readonly int[] FrameMs = { 700, 200, 130 };

    private readonly NotifyIcon _icon;
    private readonly int _port;
    private readonly SynchronizationContext? _ui;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = FrameMs[0] };
    private readonly Icon?[,] _frames = new Icon[3, Frames];        // [组, 帧]
    private readonly IntPtr[,] _handles = new IntPtr[3, Frames];    // HICON（Icon.Dispose 不回收句柄，见 Dispose）
    private AnimMode _mode = AnimMode.Idle;
    private int _frame;

    public event Action? ExitRequested;
    public event Action? RestartRequested;

    public TrayIcon(int port)
    {
        _port = port;
        var menu = new ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => OpenBrowser());
        menu.Items.Add("重启", null, (_, _) => RestartRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Text = "VibeMate",
            ContextMenuStrip = menu,
            Visible = true,                   // 先挂托盘（默认图标闪一帧），下面立刻画第一帧
        };
        // ★ 必须在创建过控件（ContextMenuStrip 即是）之后再取：WindowsForms 的
        //   同步上下文是首个控件构造时装上的，太早取会是 null
        _ui = SynchronizationContext.Current;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenBrowser(); };
        _anim.Tick += (_, _) => Tick();
        BuildFrames();
        _anim.Start();                        // 常开：待机呼吸也是循环，三态共用这一个定时器
        Show(AnimMode.Idle, 0);
    }

    private void OpenBrowser()
    {
        try
        {
            // ★ 提权进程直接 Process.Start(url) 在部分浏览器上被静默拒绝
            //   （浏览器不继承管理员）—— 用 explorer.exe 中转：它永远以普通
            //   权限跑，代开系统默认浏览器，对两种权限的主进程都成立。
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"http://127.0.0.1:{_port}/",
                UseShellExecute = true,
            });
        }
        catch { /* 关联被破坏等极端情况：不打扰用户 */ }
    }

    /// <summary>气泡通知（断连自愈提示用）。v2 的"不弹窗打扰"原则下唯一的 UI 出口。</summary>
    public void Toast(string title, string body, int ms = 3000)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(ms);
    }

    /// <summary>更新提示文字（图标形态由宫格素材决定，不随连接状态变色）。</summary>
    public void SetState(string tip)
    {
        Post(() => _icon.Text = tip.Length > 63 ? tip[..63] : tip);   // NotifyIcon 上限 63 字符
    }

    /// <summary>按键映射命中动效：第三列 3 帧快闪一轮，播完回待机呼吸。</summary>
    public void PulseKey() => Post(() =>
    {
        if (_mode == AnimMode.Voice) return;  // 语音态独占（两动效并发的概率≈0，规则简单优先）
        Enter(AnimMode.KeyPulse);
    });

    /// <summary>语音输入动效：on=true 期间第二列循环；on=false 回待机呼吸。</summary>
    public void SetVoiceActive(bool on) => Post(() =>
    {
        if (on) { Enter(AnimMode.Voice); return; }
        if (_mode != AnimMode.Voice) return;  // 声浪本来就没在放（按键闪动让它放完）
        Enter(AnimMode.Idle);
    });

    /// <summary>切形态：定时器常开，这里只重置帧号与节奏（各切换点共用）。</summary>
    private void Enter(AnimMode m)
    {
        _mode = m;
        _frame = 0;
        _anim.Interval = FrameMs[(int)m];
    }

    private void Tick()
    {
        switch (_mode)
        {
            case AnimMode.Idle or AnimMode.Voice:
                Show(_mode, _frame);
                _frame = (_frame + 1) % Frames;
                break;
            case AnimMode.KeyPulse:
                _frame++;
                if (_frame >= Frames) { Enter(AnimMode.Idle); Show(AnimMode.Idle, 0); }
                else Show(AnimMode.KeyPulse, _frame);
                break;
        }
    }

    /// <summary>外部线程 → UI 线程（拿不到上下文就直接跑 —— 只在无 WinForms 的极端场景）。</summary>
    private void Post(Action a)
    {
        if (_ui is not null) _ui.Post(_ => a(), null);
        else a();
    }

    // ---------------- 素材装载 ----------------

    private void BuildFrames()
    {
        try
        {
            using var sheet = LoadResourceBitmap();
            if (sheet is null) { BuildFallbackFrames(); return; }
            // 素材已是规整宫格（品红底素材经管线归一化：抠像+逐图标居中重排，
            // 见 gitignore 的处理脚本；各帧位置/大小齐整）—— 直接整格映射即可
            var tw = sheet.Width / 3;
            var th = sheet.Height / 3;
            for (var col = 0; col < 3; col++)          // 列 = 状态组：0 待机 / 1 语音 / 2 按键
                for (var row = 0; row < Frames; row++) // 行 = 帧
                {
                    var h = RenderFrame(sheet, col * tw, row * th, tw, th);
                    _handles[col, row] = h;
                    _frames[col, row] = Icon.FromHandle(h);
                }
        }
        catch (Exception e)
        {
            Program.Log("TRAY", $"宫格帧渲染失败，退回简笔兜底：{e.Message}");
            BuildFallbackFrames();
        }
    }

    private static Bitmap? LoadResourceBitmap()
    {
        try
        {
            using var st = typeof(TrayIcon).Assembly.GetManifestResourceStream("VibeMate.tray.icons.png");
            return st is null ? null : new Bitmap(st);
        }
        catch { return null; }
    }

    /// <summary>切一格：整格 → 画布（格→IconSize 统一变换）。大小/位置恒定 ——
    /// 帧间齐整由素材管线保证（归一化宫格），运行时零补偿。</summary>
    private static IntPtr RenderFrame(Bitmap sheet, int x, int y, int w, int h)
    {
        using var bmp = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(sheet,
                new RectangleF(0, 0, IconSize, IconSize),
                new RectangleF(x, y, w, h), GraphicsUnit.Pixel);
        }
        return bmp.GetHicon();
    }

    /// <summary>兜底帧（素材缺失/损坏时）：三组各三帧的简笔圆点 —— 保住三态语义
    ///（托盘还在动、动效可区分），别让托盘裸奔。</summary>
    private void BuildFallbackFrames()
    {
        var palette = new[]
        {
            new[] { Color.SteelBlue, Color.CornflowerBlue, Color.SteelBlue },                        // 待机：蓝呼吸
            new[] { Color.FromArgb(255, 122, 69), Color.DarkOrange, Color.FromArgb(255, 122, 69) },   // 语音：橙脉动
            new[] { Color.DodgerBlue, Color.White, Color.DodgerBlue },                                // 按键：快闪
        };
        for (var c = 0; c < 3; c++)
            for (var f = 0; f < Frames; f++)
            {
                var h = DrawDot(palette[c][f]);
                _handles[c, f] = h;
                _frames[c, f] = Icon.FromHandle(h);
            }
    }

    private static IntPtr DrawDot(Color color)
    {
        using var bmp = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color);
        g.FillEllipse(b, 6, 6, 20, 20);
        using var w = new SolidBrush(Color.White);
        g.FillEllipse(w, 14, 14, 4, 4);
        return bmp.GetHicon();
    }

    // ---------------- 上屏 ----------------

    private void Show(AnimMode mode, int frame)
    {
        var ic = _frames[(int)mode, frame];
        if (ic is not null) _icon.Icon = ic;  // 帧已缓存：换帧只是引用赋值，零分配
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _anim.Stop();
        _anim.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        for (var c = 0; c < 3; c++)
            for (var f = 0; f < Frames; f++)
            {
                _frames[c, f]?.Dispose();                                        // 只释放包装
                if (_handles[c, f] != IntPtr.Zero) DestroyIcon(_handles[c, f]);  // 句柄自己收
            }
    }
}
