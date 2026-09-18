using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeMate;

internal static class Program
{
    private const int LockPort = 49741;          // 单实例锁（对齐 v1：只在 127.0.0.1）
    private static string _baseDir = AppContext.BaseDirectory;
    private static readonly string LogPath = Path.Combine(_baseDir, "vibe.log");

    /// <summary>csproj <Version> 编译注入的程序集版本 —— ping/state 的唯一版本源。
    /// 别再手写 "2.1.x" 字符串：v2.1.1 时代 ping/state 两处硬编码，发版忘了同步，
    /// 状态页版本就停在旧号（用户实测踩坑）。</summary>
    internal static string AppVersion =>
        typeof(Program).Assembly.GetName().Version is { } v ? v.ToString(3) : "?";

    [STAThread]
    private static int Main(string[] args)
    {
        // 提权一次性注入子进程（普通权限主程序通过 runas 拉起，注入完即退）
        if (args.Length > 0 && args[0] == "--inject-only")
            return KeysRole.RunInjectOnce(args);

        // 提权一次性安装 VB-CABLE 子进程（普通权限主程序 runas 拉起，装完即退）
        if (args.Length > 0 && args[0] == "--install-cable")
        {
            var (okC, msgC) = CableSetup.InstallAsync(LogAs("CABLE")).GetAwaiter().GetResult();
            Log("CABLE", $"--install-cable 结果：{(okC ? "成功" : "失败")} {msgC}");
            return okC ? 0 : 1;
        }

        // 提权一次性卸载 VB-CABLE 子进程（同上；卸载标记 cable_optout 由发起方
        // 的主进程先写进 config —— 子进程没有 ConfigService，不抢这份职责）
        if (args.Length > 0 && args[0] == "--uninstall-cable")
        {
            var (okU, msgU) = CableUninstall.UninstallAsync(LogAs("CABLE")).GetAwaiter().GetResult();
            Log("CABLE", $"--uninstall-cable 结果：{(okU ? "成功" : "失败")} {msgU}");
            return okU ? 0 : 1;
        }

        // 提权一次性建计划任务子进程（install.ps1 / deploy.ps1 共用）：
        // schtasks 的参数只在 HttpServer 存一份
        if (args.Length > 0 && args[0] == "--setup-task")
        {
            var okT = HttpServer.RecreateAutostart();     // 强制覆盖（提权上下文无 UAC）
            Log("INFO", $"--setup-task 结果：{(okT ? "成功" : "失败")}");
            return okT ? 0 : 1;
        }

        // 目录修正：dotnet run / 单文件发布时 BaseDirectory 可能指向 bin 或
        // 解包目录 —— 约定：配置/网页/日志放在 exe 真实位置
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                _baseDir = Path.GetDirectoryName(exe)!;
        }
        catch { /* 保持默认 */ }

        // 无控制台（WinExe）→ 未处理异常必须落盘，否则"双击没反应"毫无线索（v1 铁律）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("FATAL", e.ExceptionObject?.ToString() ?? "(null)");
        Application.ThreadException += (_, e) =>
            Log("FATAL", e.Exception.ToString());

        if (!TakeSingletonLock())
        {
            // 重试而非立即退出：首次初始化的「让位重启」场景里，旧实例正在退出、
            // 锁马上释放 —— 新实例（计划任务提权版）等它一下即可接力
            var got = false;
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                if (TakeSingletonLock()) { got = true; break; }
            }
            if (!got)
            {
                Log("INFO", "已有实例在跑，退出");
                return 0;
            }
        }

        // 首次初始化（非管理员 && 无计划任务）：runas schtasks 建最高权限计划任务
        //（弹一次 UAC；exe 自身无法提权——Windows 只允许「以管理员重启自己」或
        //  拉提权子进程，这里选后者：让 schtasks 以管理员建任务，无需任何外部脚本）。
        // 任务路径取 Environment.ProcessPath —— 换目录/改名的 exe 一样成立。
        if (!HttpServer.AutostartQuery() && !HttpServer.IsAdmin())
        {
            Log("INFO", "首次启动：创建计划任务（弹一次 UAC），完成后以最高权限重启自己");
            var okInit = HttpServer.AutostartCreate();
            if (okInit)
            {
                HttpServer.RunViaTask();
                Log("INFO", "初始化完成，本实例让位给计划任务实例，退出");
                return 0;               // 锁随进程释放，新实例的重试循环会接上
            }
            Log("INFO", "初始化的 UAC 被拒绝 —— 以普通权限继续（按键注入时还会弹）");
        }

        var config = new ConfigService(Path.Combine(_baseDir, "config.json"));
        var port = config.Get("ui_port", 8787);

        // 释放随行文件（一键安装脚本/试用说明）：单 exe 分发时的自助安装入口。
        // ★ 每次启动都覆盖 —— 脚本跟 exe 版本走，不沉淀旧版
        ExtractCompanions();

        KeysInput.OnError += msg => Log("KEYS", msg);

        var voice = new VoiceRole(
            async msg => { Log("VOICE", msg); await Task.CompletedTask; },
            () =>
            {
                var t = KeysRole.Mac12(config.Get("remote_addr", ""));
                return ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                                      null, out var a) ? a : null;
            },
            () => config.Snapshot()["voice"]?.DeepClone(),
            () => config.Snapshot()["audio"]?.DeepClone());

        // 学习模式事件缓冲（/api/keyevents 轮询）
        var events = new List<(int idx, ushort usage, bool down)>();
        var evLock = new object();
        var evIdx = 0;
        void OnKeyEvent(ushort usage, bool down)
        {
            lock (evLock)
            {
                events.Add((++evIdx, usage, down));
                if (events.Count > 200) events.RemoveRange(0, events.Count - 200);
            }
        }
        JsonObject EventsSince(int since)
        {
            lock (evLock)
            {
                var arr = new JsonArray();
                foreach (var (i, u, d) in events.Where(e => e.idx > since))
                    arr.Add(new JsonObject
                    {
                        ["i"] = i,
                        ["usage"] = $"0x{u:X4}",
                        ["name"] = KeysRole.UsageName(u),
                        ["down"] = d,
                    });
                return new JsonObject { ["events"] = arr, ["last"] = evIdx };
            }
        }

        var keys = new KeysRole(
            async msg => { Log("KEYS", msg); await Task.CompletedTask; },
            () => config.Snapshot()["keys"]!.AsObject(),
            () => config.Get("remote_addr", ""),
            // devices 键与 remote_addr 同格式（带冒号 MAC），SetDevice 写入方保证一致
            () => (config.Snapshot()["devices"] as JsonObject)?[config.Get("remote_addr", "")] as JsonObject);
        keys.KeyEvent += OnKeyEvent;
        keys.Activity += voice.Poke;          // 遥控器按键活动 → 语音立刻重连
        voice.VoiceKeyEvent += down => keys.FireVirtual(0xFFFE, down);   // 语音键 → 虚拟 usage

        var http = HttpServer.Bind(port, Path.Combine(_baseDir, "web"), LogPath, config,
                                  () => new JsonObject
                                  {
                                      ["ok"] = true,
                                      ["version"] = AppVersion,
                                      ["pid"] = Environment.ProcessId,
                                      ["port"] = port,
                                      ["voice"] = new JsonObject
                                      {
                                          ["connected"] = voice.Connected,
                                          ["note"] = voice.Note,
                                      },
                                      ["keys"] = new JsonObject
                                      {
                                          ["ready"] = keys.Ready,
                                          ["note"] = keys.Note,
                                          ["stats"] = new JsonObject { ["total"] = keys.HookTotal },
                                      },
                                      ["learn"] = keys.LearnSnapshot(),
                                      ["cable"] = new JsonObject
                                      {
                                          ["installed"] = CableSetup.Installed(),
                                          // 卸载进度/重启提醒（null = 无卸载可报）——
                                          // 主进程与提权子进程经 ProgramData 状态文件互通
                                          ["uninstall"] = CableUninstall.SnapshotUninstall(),
                                      },
                                      ["update"] = UpdateCheck.Snapshot(),
                                      ["config"] = config.Snapshot(),
                                  },
                                  EventsSince,
                                  keys);
        // 端口顺延要广而告之：托盘提示/状态页虽显示实际地址，用户背的可能是配置号
        if (http.Port != port)
        {
            Log("INFO", $"UI 端口 {port} 被占 —— 已顺延到高位端口 {http.Port}");
            port = http.Port;      // stateBuilder 闭包按引用捕获，这里改完页面即报新号
        }
        else if (!http.Running)
            Log("FATAL", $"UI 端口 {port} 与高位段 50 个候选全被占 —— 控制台不可用（语音/按键不受影响）");
        voice.VoiceKeyEnabled = config.Snapshot()["keys"]?["voice"] is null;
        keys.ReloadMapping();               // 启动即装载映射表（ConfigChanged 只覆盖后续变更）
        keys.ReloadProfile();               // 启动即装载报告指纹（同上）
        DeviceDb.RefreshAsync();            // 已知设备表后台刷新（内置兜底，失败静默）
        UpdateCheck.Start();                // 版本检查后台循环（结果进 /api/state.update）
        voice.Start();                      // HTTP 已随 Bind 启动
        keys.Start();

        using var tray = new TrayIcon(port);
        // 托盘动效（素材宫格见 TrayIcon）：待机呼吸循环常开；
        // 映射命中→按键组快闪一轮；语音输入→语音组循环
        keys.MappedFire += tray.PulseKey;
        voice.Speaking += tray.SetVoiceActive;
        tray.ExitRequested += async () =>
        {
            await keys.UnloadAsync();         // 注销注入（用户验收点）
            Application.Exit();
        };
        tray.RestartRequested += async () => await RestartAsync();
        tray.SetState($"VibeMate — http://127.0.0.1:{port}");

        // 托盘「重启」：卸 hook + 释放蓝牙会话再拉新实例（完整走一遍生命周期，
        // 新实例自己重新注入 —— 治各种「注入烂了/蓝牙会话烂了」的疑难杂症）
        async Task RestartAsync()
        {
            Log("INFO", "托盘：重启 —— 停按键主循环 → 卸载注入 → 释放语音 → 拉新实例");
            try
            {
                keys.Dispose();                // 先停主循环（否则 2s 内它会重注入）
                await keys.UnloadAsync();      // 再发 unload：DLL 真正注销（验收点）
                voice.Dispose();               // BLE 单客户端铁律：旧会话必须关干净
            }
            catch (Exception e) { Log("INFO", $"重启清理异常（继续）：{e.Message}"); }
            http.Stop();                       // 先放端口，新实例才绑得上
            await Task.Delay(600);
            if (TrySpawnNewInstance()) { Application.Exit(); return; }
            // 两手都失败：不能裸退（等于杀掉用户的服务）—— 复活 HTTP 继续跑
            Log("INFO", "重启失败：新实例没拉起来，本实例继续服务");
            http.Start();
            if (!http.Running)
                Log("INFO", "复活旧实例的 HTTP 失败（端口被占？）—— 控制台暂不可用");
            tray.Toast("重启失败", "新实例没有拉起来，已恢复当前实例（日志有详情）");
        }

        // 拉起新实例的两条路：直接子进程（继承当前令牌，管理员→管理员，无 UAC；
        // 新实例自带单实例锁重试 15s，能接上正在退出的旧实例）→ 计划任务兜底
        //（exe 路径拿不到/权限异常时；任务=最高权限，起来就是管理员）。
        bool TrySpawnNewInstance()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    using var p = Process.Start(new ProcessStartInfo
                    { FileName = exe, UseShellExecute = false, CreateNoWindow = true });
                    if (p is not null) return true;
                }
            }
            catch (Exception e) { Log("INFO", $"直接拉起新实例失败：{e.Message}"); }
            try { HttpServer.RunViaTask(); return true; }
            catch (Exception e) { Log("INFO", $"计划任务拉起也失败：{e.Message}"); return false; }
        }

        // 虚拟声卡看护：没装 VB-CABLE → 管理员下全自动静默装（驱动内嵌 exe）；
        // 普通权限不弹 UAC 打扰 —— 界面语音页有手动按钮（使用说明在程序内
        // 「安装帮助」页，不再单独发 txt：说明只养一份，跟着 exe 走不漂移）
        // ★ cable_optout：用户从界面卸载过 → 跳过。不看这个标记的话，下次开机
        //   看护会把用户刚卸的东西原样装回来 —— 在用户眼里就是「卸不掉的 bug」
        //  （v1 千问开关的同款教训）。想装回：语音页「检测并安装」即清除标记。
        _ = Task.Run(async () =>
        {
            try
            {
                if (config.Get("cable_optout", false))
                { Log("CABLE", "用户已卸载虚拟声卡（cable_optout）—— 跳过自动安装"); return; }
                if (CableSetup.Installed()) return;
                if (!HttpServer.IsAdmin())
                {
                    Log("CABLE", "未检测到 VB-CABLE 且当前非管理员 —— 请到界面语音页手动安装");
                    return;
                }
                Log("CABLE", "未检测到 VB-CABLE —— 开始静默安装（驱动内嵌于 exe）");
                var (ok, msg) = await CableSetup.InstallAsync(LogAs("CABLE"));
                Log("CABLE", $"安装结果：{(ok ? "成功" : "失败")} {msg}");
                if (ok) tray.Toast("虚拟声卡已装好", "VB-CABLE 安装完成，语音链路已就绪");
                else tray.Toast("虚拟声卡自动安装失败",
                    "请到设置页「语音 → 虚拟声卡」手动安装（详见「安装帮助」页）");
            }
            catch (Exception e) { Log("CABLE", $"安装异常：{e.Message}"); }
        });
        voice.BadSession += n => tray.Toast("语音连接异常",
            n >= 3 ? "蓝牙会话连续异常，建议取一次遥控器电池再装回" : "蓝牙会话异常，正在自动重建…");

        config.ConfigChanged += cfg =>
        {
            Log("INFO", $"配置已更新并落盘（{cfg.Count} 项顶层字段）");
            voice.ReloadKey();                // 语音键目标即时生效
            voice.ApplyAudio();               // 增益/AGC/直通即时生效
            keys.ReloadMapping();             // 按键映射即时生效（block 表随下轮会话）
            keys.ReloadProfile();             // 报告指纹即时生效（切设备/学习保存）
            // 互斥：语音键被映射成普通键 → VoiceKey 停发（避免双发）
            voice.VoiceKeyEnabled = cfg["keys"]?["voice"] is null;
        };

        Log("INFO", $"启动完成：http://127.0.0.1:{port}（PID {Environment.ProcessId}）");
        Application.Run();     // WinForms 消息循环（托盘存活期间不返回）
        Log("INFO", "退出");
        voice.Dispose();
        keys.Dispose();
        http.Stop();
        return 0;
    }

    /// <summary>
    /// 单实例：Global Mutex（跨提权/会话可靠——实测端口锁挡不住管理员+普通双开）
    /// + 端口占位（抗僵尸，进程死=自然释放）。两者其一失败即退出。
    /// </summary>
    private static Mutex? _mutex;
    private static bool TakeSingletonLock()
    {
        _mutex = new Mutex(true, @"Global\VibeMateV2-Singleton", out var created);
        if (!created) return false;
        try
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, LockPort);
            l.Start(1);
            return true;    // 故意不 Stop：占着直到进程退出
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>释放随行文件到 exe 目录（install.ps1 / 一键安装.bat）。
    /// 清单不在这维护：按 "VibeMate.companion." 资源名前缀枚举（csproj 是唯一登记处），
    /// 前缀后的资源名即释放文件名 —— 加随行文件只改 csproj 一处。
    /// 单个失败静默 —— 只是便利文件，不能拖垮主功能。</summary>
    private static void ExtractCompanions()
    {
        try
        {
            foreach (var name in typeof(Program).Assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith("VibeMate.companion.", StringComparison.Ordinal)))
                ExtractResource(name, Path.Combine(_baseDir, name["VibeMate.companion.".Length..]));
        }
        catch { /* 枚举失败等极端场景：静默 */ }
    }

    /// <summary>内嵌资源 → 文件的唯一释放原语（Companions / tap.dll / vbcable 驱动共用）。
    /// 返回 false = 资源缺失或写盘失败（原因交调用方决定要不要说）。</summary>
    internal static bool ExtractResource(string resName, string destPath)
    {
        try
        {
            using var st = typeof(Program).Assembly.GetManifestResourceStream(resName);
            if (st is null) return false;
            using var fs = File.Create(destPath);
            st.CopyTo(fs);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把同步日志适配成角色要的 Func&lt;string,Task&gt;（CABLE 等角色共用 ——
    /// 各调用点手抄 lambda 已经抄出过三种写法，收拢成一处）。</summary>
    internal static Func<string, Task> LogAs(string tag)
        => m => { Log(tag, m); return Task.CompletedTask; };

    /// <summary>runas 拉起自身子进程（--inject-only / --install-cable 共用的一次性提权：
    /// 一次 UAC、干完即退）。false = UAC 被拒/启动失败（真实原因已入日志，调用方自己定文案）。</summary>
    internal static bool RunSelfElevated(string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
            });
            if (p is null) return false;
            p.WaitForExit(timeoutMs);          // 超时不当失败：UAC 窗口开着等用户也算正常
            return true;
        }
        catch (Exception e)
        {
            Log("INFO", $"提权子进程拉起失败：{e.Message}");
            return false;
        }
    }

    internal static void Log(string level, string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}{Environment.NewLine}";
            File.AppendAllText(LogPath, line, Encoding.UTF8);
            if (new FileInfo(LogPath).Length > 512 * 1024)   // 512K 截断（对齐 v1）
                File.WriteAllText(LogPath, line, Encoding.UTF8);
        }
        catch { /* 日盘满等极端场景：静默 */ }
    }
}
