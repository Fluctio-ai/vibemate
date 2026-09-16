using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace VibeMate;

/// <summary>
/// 按键角色 —— tap.dll 的宿主侧：定位 WUDFHost → 注入 → 管道常驻 → 报文→映射执行。
/// （里程碑①的实机结论全部内置：DLL 放 ProgramData、DACL 由 DLL 侧放开、
///   连接常驻不断开、退出时 unload。）
///
/// 普通权限下的注入：主程序不必常驻管理员 —— 用 runas 拉起自身 --inject-only
/// 子进程弹一次 UAC 完成注入后退出（对齐 v1「一键优化」的体验哲学）。
/// </summary>
public sealed class KeysRole : IDisposable
{
    // usage 语义名表（v1 keys.py:103 的反向；学习模式给未知 usage 起名用）
    internal static readonly Dictionary<string, ushort> KeyUsage = new()
    {
        ["power"] = 0x019E, ["input"] = 0x0189,
        ["up"] = 0x0042, ["down"] = 0x0043, ["left"] = 0x0044, ["right"] = 0x0045,
        ["ok"] = 0x0041, ["back"] = 0x0224, ["home"] = 0x0223,
        ["volup"] = 0x00E9, ["voldown"] = 0x00EA, ["mute"] = 0x00E2,
        ["youtube"] = 0x0077, ["netflix"] = 0x0078,
        // 语音键的虚拟 usage：它不走 HID（ATVV 私有服务），由 VoiceRole 转发
        // 进按键系统 —— 映射了它 = 显式放弃语音功能把键挪作他用
        ["voice"] = 0xFFFE,
    };
    internal static string UsageName(ushort u) =>
        KeyUsage.FirstOrDefault(p => p.Value == u).Key ?? $"usage_{u:X4}";

    private readonly Func<string, Task> _log;
    private readonly Func<JsonObject> _getKeys;
    private readonly Func<string> _getAddr;        // 当前遥控器地址（remote_addr）
    private readonly Func<JsonObject?> _getDevice; // devices[addr]：指纹来源（学习/设备表）
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private readonly Mapper _mapper = new();

    /// <summary>当前会话的管道（学习模式 start/stop 要跨线程下发 filter，存一份引用）。</summary>
    private NamedPipeClientStream? _pipe;

    /// <summary>当前报告指纹：devices[addr].report（学习产物或设备表预填）> Google 默认。
    /// 缓存值 —— HandleLine 是每份报告都走的热路径，走 _getDevice() 等于全配置
    /// 深拷贝（还要和磁盘写抢同一把配置锁）。配置变更时 ReloadProfile 重算
    /// （Program 的 ConfigChanged 处理器调用，与 ReloadMapping 同节奏）。</summary>
    internal volatile ReportProfile Profile = ReportProfile.Google;

    /// <summary>配置变更后重算指纹（切设备/学习保存即时生效）。</summary>
    public void ReloadProfile() =>
        Profile = ReportProfile.FromConfig(_getDevice()) ?? ReportProfile.Google;

    /// <summary>蓝牙地址归一化：去分隔符 → 12 位大写 hex（KeysRole/HttpServer/Program 共用）。</summary>
    internal static string Mac12(string addr) =>
        addr.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();

    /// <summary>BTHLEDevice 服务节点名（{服务GUID}_Dev_VID&amp;.._PID&amp;.._REV&amp;.._MAC）→
    /// 尾部 12 位 MAC（大写）。形态不符返回 null。KeysRole 定位宿主与 HttpServer
    /// 设备列表共用同一份注册表知识 —— 收在一处，两处行为不分叉。</summary>
    internal static string? BthleMacOf(string nodeName)
    {
        var tail = nodeName.Contains('_') ? nodeName[(nodeName.LastIndexOf('_') + 1)..] : nodeName;
        return tail.Length == 12 && tail.All(char.IsLetterOrDigit) ? tail.ToUpperInvariant() : null;
    }

    /// <summary>节点名里的 VID/PID（结构化解析；VID 带 2 位总线前缀 0218D1 → 18D1）。
    /// 比裸 Contains 子串匹配严谨 —— MAC/序列号碰巧含 "18d1""9450" 不会误中。</summary>
    private static readonly System.Text.RegularExpressions.Regex VidPidRe = new(
        @"VID&\d{2}([0-9A-Fa-f]{4})_PID&([0-9A-Fa-f]{4})",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static (string Vid, string Pid)? BthleVidPidOf(string nodeName)
    {
        var m = VidPidRe.Match(nodeName);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    public volatile bool Ready;
    public volatile string Note = "未启动";
    /// <summary>按键事件（学习模式监听用）：usage + 按下/松开。</summary>
    public event Action<ushort, bool>? KeyEvent;
    /// <summary>映射命中并已执行 —— 托盘按键动效用。语音键未改映射时映射表里
    /// 没有它（Mapper.Fire 早退），自然不触发，无需任何特判。</summary>
    public event Action? MappedFire;
    /// <summary>遥控器有活动 —— 语音角色用它立刻重连（遥控器只在按键后醒一小会）。</summary>
    public event Action? Activity;

    public KeysRole(Func<string, Task> log, Func<JsonObject> getKeys,
                    Func<string> getAddr, Func<JsonObject?> getDevice)
    {
        _log = log;
        _getKeys = getKeys;
        _getAddr = getAddr;
        _getDevice = getDevice;
        _mapper.OnExecute = () => MappedFire?.Invoke();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => MainLoop(_cts.Token))
        { IsBackground = true, Name = "KeysRole" };
        _thread.Start();
    }

    // ================= 主循环 =================
    private async void MainLoop(CancellationToken ct)
    {
        await _log("按键角色：启动");
        while (!ct.IsCancellationRequested)
        {
            // ★ 按「已选设备」定位：选中谁的 MAC 就找谁的 HID 服务节点 —— 任何品牌
            //   通吃（不再硬编码 18d1/9450）。未选设备时保持 Google 默认（行为不变）。
            var mac = Mac12(_getAddr());
            var pid = FindWudfHostPid(mac.Length == 12 ? mac : null);
            if (pid is null)
            {
                Note = "没找到遥控器的 HID 宿主（未连接？）";
                await Task.Delay(5000, ct).ContinueWith(_ => { });
                continue;
            }
            try
            {
                await Session(pid.Value, ct);
            }
            catch (Exception e)
            {
                Note = $"会话异常：{e.Message}";
                await _log($"按键：会话异常 {e.GetType().Name}: {e.Message}");
            }
            await Task.Delay(2000, ct).ContinueWith(_ => { });   // 断开后稍歇再试
        }
        await _log("按键角色：退出");
    }

    /// <summary>一次会话：确保注入 → 连管道 → 读报文直到断开。</summary>
    private async Task Session(int pid, CancellationToken ct)
    {
        // 注入并连上管道（初始路径和协议升级路径共用的序列；failCtx 进日志/Note
        // 区分走的哪条路）。null = 已置好 Note，调用方直接 return。
        async Task<NamedPipeClientStream?> InjectAndConnect(string failCtx)
        {
            if (!await EnsureInjected(pid))
            {
                Note = $"{failCtx}注入失败（UAC 被拒？）";
                return null;
            }
            var p = await TryConnect(pid, 10000, ct);
            if (p is null)
            {
                Note = $"{failCtx}注入完成但连不上管道";
                await _log($"按键：{failCtx}注入后 10s 内连不上管道");
            }
            return p;
        }

        // 0) DLL 可能已在（重启主程序/上轮注入）：先快速试连。
        //    ★ 成功判据 = 管道连得上 —— WaitNamedPipe 的 0 超时语义不可靠，
        //    直接以连接为准，一网打尽所有边缘情况。
        var pipe = await TryConnect(pid, 500, ct);
        if (pipe is null)
            pipe = await InjectAndConnect("");
        if (pipe is null) return;
        // ★ 协议握手（tap 3）：WUDFHost 常驻 —— 主程序部署新版后，管道里连到的
        //   可能还是旧版驻留 DLL（管道名不变；旧协议不认识 filter/block 偏移，
        //   学习模式开了也收不到报告）。握手不符 = 卸旧注新，一次自动升级。
        var hello = await ReadHello(pipe, ct);
        if (hello != "tap 3")
        {
            await _log($"按键：DLL 协议握手不符（{hello ?? "(无)"}）—— 卸载旧版并升级注入");
            try
            {
                var bye = Encoding.ASCII.GetBytes("unload\n");
                await WritePipeAsync(pipe, bye);
                await Task.Delay(1500, ct);       // 等 DLL 摘 hook + 自卸（文件锁释放）
            }
            catch { /* DLL 已不在 = 无需升级 */ }
            pipe.Dispose();
            lock (_learnLock) { _learn = null; _pipe = null; }   // 协议变了，学习作废重来
            pipe = await InjectAndConnect("tap.dll 升级");
            if (pipe is null) return;
            hello = await ReadHello(pipe, ct);
            if (hello != "tap 3")
            {
                Note = "tap.dll 协议握手失败";
                await _log($"按键：升级后握手仍不符（{hello ?? "(无)"}）");
                pipe.Dispose();
                return;
            }
        }
        using var _ = pipe;
        lock (_learnLock) _pipe = pipe;          // 学习 API 跨线程下发 filter 用
        try
        {
        Note = "已连接";
        Ready = true;
        await _log($"按键：管道已连接（WUDFHost {pid}）");

        // 下发当前指纹（学习进行中则全量上报）+ 已映射键的清位表
        await WritePipeAsync(pipe, FilterBytes());
        await SendBlockAsync(pipe);

        // 2) 报文循环
        using var reader = new StreamReader(pipe, Encoding.ASCII);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;                        // DLL 断开
            HandleLine(pipe, line);
        }
        Ready = false;
        Note = "连接断开";
        _mapper.ReleaseAll();
        await _log("按键：连接断开（映射全部抬起）");
        }
        finally { lock (_learnLock) _pipe = null; }
    }

    private static async Task<NamedPipeClientStream?> TryConnect(
        int pid, int timeoutMs, CancellationToken ct)
    {
        // ★ 管道名 vibemote-keys-<pid> 是 tap.dll 里写死的契约（DLL 侧创建同名
        //   管道）—— 项目改名 VibeMate 也不动它，动了就再也连不上
        var s = new NamedPipeClientStream(".", $"vibemote-keys-{pid}", PipeDirection.InOut);
        try { await s.ConnectAsync(timeoutMs, ct); return s; }
        catch { s.Dispose(); return null; }
    }

    /// <summary>连接后读 DLL 第一行（协议握手 "tap 3"；2s 超时）。
    /// null=超时/断开；""=空行。绝不能关流（管道还要继续用）。</summary>
    private static async Task<string?> ReadHello(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            using var tcs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcs.CancelAfter(2000);
            using var sr = new StreamReader(pipe, Encoding.ASCII, false, 1024, leaveOpen: true);
            return await sr.ReadLineAsync(tcs.Token);
        }
        catch { return null; }
    }

    private void HandleLine(NamedPipeClientStream pipe, string line)
    {
        if (line.StartsWith("report "))
        {
            // 变长协议：report <len> <b0> <b1> ...（DLL 按下发的指纹过滤过了，
            // 这里再校验一道 —— 指纹可能在会话中途被配置更新改掉）
            var parts = line[7..].Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var n)
                || n != parts.Length - 1 || n is < 1 or > 64)
                return;
            var b = new byte[n];
            for (var i = 0; i < n; i++)
                if (!byte.TryParse(parts[i + 1], System.Globalization.NumberStyles.HexNumber,
                                   null, out b[i]))
                    return;
            Activity?.Invoke();
            lock (_learnLock)
            {
                if (_learn is { } ls)
                {
                    // 排障观测点：学习识别不到按键时，先看有没有走到这里 ——
                    // 没有这行日志 = DLL 根本没上报（hook/过滤问题）
                    if (!ls.SeenAny)
                    {
                        ls.SeenAny = true;
                        _ = _log($"按键：学习首份报告 len={b.Length} [{string.Join(' ', b.Select(x => x.ToString("X2")))}]");
                    }
                    ls.Feed(b); return;  // 学习中：只收集不映射
                }
            }
            var p = Profile;
            if (n != p.Len || b[0] != (byte)p.Id || p.Off + p.W > n) return;
            var usage = (ushort)(p.W == 1 ? b[p.Off] : (b[p.Off] | (b[p.Off + 1] << 8)));
            if (usage == 0)
                _mapper.ReleaseAll();                    // 空闲帧：全部松开
            else
            {
                KeyEvent?.Invoke(usage, true);
                _mapper.Fire(usage);
            }
        }
        else if (line.StartsWith("hb "))
        {
            var p = line[3..].Split(' ');
            if (p.Length == 3 && long.TryParse(p[0], out var total))
            {
                _mapper.NoteStats(total);
                // 学习排障观测点：total=流经 hook 的报告数（20s 一拍）。学习时
                // total 不涨 = DLL 没捕获到这台设备的读路径；涨但无 report = 过滤/变化判定问题
                if (IsLearning)
                    _ = _log($"按键：学习心跳 total={total} sent={p[1]} blocked={p[2]}");
            }
        }
    }

    /// <summary>当前 filter 命令字节；学习进行中则 filter 0 0（DLL 全量上报，绝不清位）。</summary>
    private byte[] FilterBytes() =>
        Encoding.ASCII.GetBytes((IsLearning ? "filter 0 0" : $"filter {Profile.Len} {Profile.Id:x2}") + "\n");

    /// <summary>带超时的管道写（全文件管道写的唯一入口 —— unload/退出路径同受保护，
    /// 烂 DLL 对端不读时没有豁免）。★绝不用无限期同步 Write：对端不读、缓冲写满，
    /// Write 会永久挂起 —— 调用方在 HTTP 单线程 Loop 上就是全站按钮卡死，在
    /// Session 里就是断线重连自愈失效。超时即抛，让上层断开重来。</summary>
    private const int PipeWriteTimeoutMs = 500;

    private static async Task WritePipeAsync(NamedPipeClientStream pipe, byte[] bytes)
    {
        using var cts = new CancellationTokenSource(PipeWriteTimeoutMs);
        await pipe.WriteAsync(bytes, 0, bytes.Length, cts.Token);
    }

    private async Task SendBlockAsync(NamedPipeClientStream pipe)
    {
        var keys = _getKeys();
        var p = Profile;
        var us = keys.Where(k => KeyUsage.ContainsKey(k.Key))
                     .Select(k => KeyUsage[k.Key])
                     .Select(u => u.ToString("x4"));
        // 清位槽偏移/宽度跟指纹走（Google=b[1..2] LE16，RC003 类=b[3] 单字节）
        var cmd = Encoding.ASCII.GetBytes(
            $"block @{p.Off}:{p.W} " + string.Join(' ', us) + "\n");
        await WritePipeAsync(pipe, cmd);
    }

    // ================= WUDFHost 定位（v1 keys.py:351 注册表法）=================
    // ★ 通用化（2026-09-16）：按「已选设备的 MAC」匹配 BTHLEDevice 节点尾部 ——
    //   选中哪个设备就注入它所在的宿主，任何品牌通吃，vid/pid 不再硬编码；
    //   未选设备（remote_addr 空）时退回 Google 18d1/9450（v2 以来行为不变，
    //   存量用户升级不断档；用结构化 VID/PID 解析而非裸子串，防误中）。
    private static int? FindWudfHostPid(string? mac12)
    {
        const string svcPrefix = "{00001812-0000-1000-8000-00805f9b34fb}";
        using var root = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice");
        if (root is null) return null;
        foreach (var svcName in root.GetSubKeyNames())
        {
            var low = svcName.ToLowerInvariant();
            if (!low.StartsWith(svcPrefix)) continue;
            if (mac12 is { Length: 12 })
            {
                if (BthleMacOf(svcName) != mac12) continue;
            }
            else if (BthleVidPidOf(low) is not { } vp
                     || !vp.Vid.Equals("18d1", StringComparison.OrdinalIgnoreCase)
                     || !vp.Pid.Equals("9450", StringComparison.OrdinalIgnoreCase))
                continue;
            using var svc = root.OpenSubKey(svcName);
            if (svc is null) continue;
            foreach (var inst in svc.GetSubKeyNames())
            {
                using var dk = svc.OpenSubKey($@"{inst}\Device Parameters\WUDFDiagnosticInfo");
                if (dk?.GetValue("HostPid") is { } raw)
                {
                    // 实测 REG_DWORD（.NET 里是 int）；防御性兼容 long/string
                    var v = raw switch
                    {
                        int i => i,
                        long l => (int)l,
                        string s when int.TryParse(s, out var p) => p,
                        _ => 0,
                    };
                    if (v > 0) return v;
                }
            }
        }
        return null;
    }

    // ================= 注入 =================
    private async Task<bool> EnsureInjected(int pid)
    {
        // ★ 判定注入成功用「管道出现」而不是模块枚举：普通权限的 Toolhelp32
        //   快照对 Session 0 沙箱进程枚举不到模块（管理员子进程却看得到）——
        //   用枚举判定会造成「注入成功却反复重注入」的 UAC 轰炸。
        if (WaitNamedPipeW($@"\\.\pipe\vibemote-keys-{pid}", 0)) return true;
        if (IsModuleLoaded(pid, "tap.dll")) return true;

        // 防抖：UAC 弹窗冷却 30 秒，绝不轰炸
        if (Environment.TickCount64 - _lastInjectTry < 30000) return false;
        _lastInjectTry = Environment.TickCount64;

        // DLL 部署到 ProgramData\VibeMate（服务账户可读；用户目录读不了 —— 实测坑）
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VibeMate");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "tap.dll");
        // tap.dll 嵌在 exe 资源里（防用户删改物理文件）—— 从资源释放
        // 写失败也可能是 tap.dll 已被 WUDFHost 加载锁定（注入其实成功了）→ 权威判据 = 模块枚举
        if (!Program.ExtractResource("VibeMate.tap.dll", dll) && !IsModuleLoaded(pid, "tap.dll"))
        {
            // 目标宿主没有 DLL 且新副本写不进 —— 大概率「别的」旧 WUDFHost 锁着
            // 文件（遥控器重连后换了宿主）。磁盘 DLL 还在：LoadLibrary 共享读
            // 不受锁影响，协议握手（tap 3）兜得住版本差异 —— 直接注入现有文件。
            if (!File.Exists(dll))
            {
                Note = "缺少内嵌 tap.dll";
                await _log("按键：tap.dll 资源释放失败");
                return false;
            }
            await _log("按键：tap.dll 被旧宿主锁定无法更新 —— 注入磁盘现有副本");
        }
        Process.Start(new ProcessStartInfo("icacls", $"\"{dll}\" /grant Everyone:RX")
        { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000);

        if (new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator))
            InjectNow(pid, dll);
        else
        {
            // 普通权限：runas 拉起自身做一次性注入（一次 UAC），主程序保持普通权限
            await _log("按键：注入需要管理员 —— 弹一次 UAC（提权只用于这一次注入）");
            if (!Program.RunSelfElevated($"--inject-only {pid} \"{dll}\"", 30000))
            {
                await _log("按键：UAC 被拒绝 —— 30 秒后再试");
                return false;
            }
        }

        // 权威判据：管道就绪（普通权限可探测；注意要完整 \\.\pipe\ 前缀）
        for (int i = 0; i < 50; i++)
        {
            if (WaitNamedPipeW($@"\\.\pipe\vibemote-keys-{pid}", 0)) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private long _lastInjectTry;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, int timeout);

    /// <summary>--inject-only 入口：注入后立即退出（由提权子进程执行）。</summary>
    public static int RunInjectOnce(string[] args)
    {
        // 无 UI 无控制台：结果落盘，主进程/用户可查
        var logFile = Path.Combine(AppContext.BaseDirectory, "inject.log");
        void LogI(string m) => File.AppendAllText(logFile,
            $"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}");
        try
        {
            if (args.Length < 3 || !int.TryParse(args[1], out var pid))
            {
                LogI($"参数不对：{string.Join(' ', args)}");
                return 2;
            }
            LogI($"提权注入开始：pid={pid} dll={args[2]}");
            var ok = InjectNow(pid, args[2]);
            LogI($"InjectNow => {ok}");
            LogI($"模块在目标进程: {IsModuleLoaded(pid, "tap.dll")}");
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            LogI($"异常 {e}");
            return 3;
        }
    }

    // ---- Win32 注入原语（与 _tap/inject.py 同逻辑，含 64 位 restype 纪律）----
    private static class Native
    {
        public const uint PROCESS_ALL = 0x1F0FFF;
        public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_RW = 0x04;
        public const uint TH32CS_SNAPMODULE = 8, TH32CS_SNAPMODULE32 = 0x10;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, nint size,
                                                   uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf,
                                                     nint size, out nint written);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, nint size,
                                                       IntPtr start, IntPtr param, uint flags,
                                                       out uint tid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr h, out uint code);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool LookupPrivilegeValueW(string? sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll,
            ref TOKEN_PRIVILEGES newp, int len, IntPtr prev, IntPtr ret);

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }
        [DllImport("kernel32.dll")]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32 me);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32 me);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MODULEENTRY32
        {
            public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
        }
    }

    internal static bool IsModuleLoaded(int pid, string dllName)
    {
        var snap = Native.CreateToolhelp32Snapshot(
            Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return false;
        var me = new Native.MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32>() };
        var found = false;
        if (Native.Module32FirstW(snap, ref me))
            do { if (me.szModule.Equals(dllName, StringComparison.OrdinalIgnoreCase)) { found = true; break; } }
            while (Native.Module32NextW(snap, ref me));
        Native.CloseHandle(snap);
        return found;
    }

    /// <summary>启用 SeDebugPrivilege（注入服务/沙箱进程的前提）。</summary>
    private static void EnableSeDebugPrivilege(Action<string> log)
    {
        try
        {
            if (!Native.OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var tok))
                { log($"OpenProcessToken 失败 err={Marshal.GetLastWin32Error()}"); return; }
            if (!Native.LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
                { log("LookupPrivilegeValue 失败"); Native.CloseHandle(tok); return; }
            var tp = new Native.TOKEN_PRIVILEGES
            { PrivilegeCount = 1, Luid = luid, Attributes = 0x2 /*SE_PRIVILEGE_ENABLED*/ };
            Native.AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            log($"SeDebugPrivilege => {Marshal.GetLastWin32Error() == 0}");
            Native.CloseHandle(tok);
        }
        catch (Exception e) { log($"SeDebugPrivilege 异常 {e.Message}"); }
    }

    private static bool InjectNow(int pid, string dllPath)
    {
        void Step(string m) => File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "inject.log"),
            $"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}");
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        Step($"InjectNow pid={pid} admin={admin} exe={Environment.ProcessPath}");
        EnableSeDebugPrivilege(Step);     // WUDFHost 是沙箱化的服务进程：不启用它
                                          // 连管理员 OpenProcess 都会被 DACL 拒掉
        var ph = Native.OpenProcess(Native.PROCESS_ALL, false, pid);
        Step($"OpenProcess => {ph} err={Marshal.GetLastWin32Error()}");
        if (ph == IntPtr.Zero) return false;
        try
        {
            var path = Encoding.Unicode.GetBytes(dllPath + "\0");
            var addr = Native.VirtualAllocEx(ph, IntPtr.Zero, path.Length,
                                             Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_RW);
            Step($"VirtualAllocEx => {addr}");
            if (addr == IntPtr.Zero) return false;
            if (!Native.WriteProcessMemory(ph, addr, path, path.Length, out _))
            { Step("WriteProcessMemory 失败"); return false; }
            var llw = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            var th = Native.CreateRemoteThread(ph, IntPtr.Zero, 0, llw, addr, 0, out _);
            Step($"CreateRemoteThread => {th}");
            if (th == IntPtr.Zero) return false;
            Native.WaitForSingleObject(th, 15000);
            Native.GetExitCodeThread(th, out var code);
            Native.CloseHandle(th);
            Step($"远程 LoadLibraryW 退出码 => 0x{code:X8}");
            // 64 位 HMODULE 可能落在任何区间，退出码无法可靠区分成败：
            // 真判据 = 模块枚举（调用方 EnsureInjected 已在用）
            return code != 0;
        }
        finally { Native.CloseHandle(ph); }
    }

    /// <summary>退出前注销注入（用户验收点）。</summary>
    public async Task UnloadAsync()
    {
        try
        {
            var mac = Mac12(_getAddr());
            var pid = FindWudfHostPid(mac.Length == 12 ? mac : null);
            if (pid is null) return;
            var pipe = new NamedPipeClientStream(".", $"vibemote-keys-{pid}", PipeDirection.InOut);
            await pipe.ConnectAsync(1000);
            var cmd = Encoding.ASCII.GetBytes("unload\n");
            await WritePipeAsync(pipe, cmd);
            await Task.Delay(1500);                    // 给 DLL 走完注销流程
            pipe.Dispose();
            await _log("按键：unload 已发送，DLL 已注销");
        }
        catch { /* DLL 已不在/管道不通 = 无需注销 */ }
    }

    public void ReloadMapping() => _mapper.Load(_getKeys());

    /// <summary>虚拟键触发（语音键等非 HID 来源）：down=Fire / up=Release。</summary>
    public void FireVirtual(ushort usage, bool down)
    {
        KeyEvent?.Invoke(usage, down);
        if (down) _mapper.Fire(usage);
        else _mapper.Release(usage);
    }

    public void Dispose()
    {
        // 幂等：托盘「重启」会先 Dispose 停主循环（防它马上重注入）再 UnloadAsync，
        // Main 收尾还会再进一次 —— 重复 Cancel 已释放的 cts 会抛 ObjectDisposed
        var cts = _cts; _cts = null;
        cts?.Cancel();
        try { _thread?.Join(2000); } catch { }
        _thread = null;
        cts?.Dispose();
    }

    // ================= 学习模式（未知遥控器接入：串行单键 × 3 次确认）=================
    //
    // 用户流程（2026-09-16 定稿）：开始 → 按一个键 → 展示「1/3」→ 继续按同一个键
    // → 每按一次计数 +1 → 满 3 次且报文一致 = 确认该键 → 前端打开表单起名+配动作
    // → 保存后 next 学下一个键；期间出现不同报文 = 重置回 waiting（GotOther 带
    // 出原因供前端提示，学习不中断，不用重新点开始）。
    // 学习期间 DLL 全量上报且绝不清位（filter 0 0）—— 全量流里混着同一宿主其他
    // 蓝牙键鼠的报告，动了会弄坏人家的输入。
    //
    // 指纹自动推导：每组「按下→松开」对比（等长、ReportID 相同、恰好 1-2 个相邻
    // 字节不同 = usage 槽）。1 字节不同时的宽度歧义按长度启发：len≤3 的 Consumer
    // 报告惯例是 16bit usage（Google 遥控器 02 xx yy，高字节为 0 时 diff 只见 1 字节
    // —— 取 w=2 才能对上映射表里 0x0224 这类值）；len>3 的键盘报告是单字节槽
    // （小米 RC003 的 01 … key1 在 b[3]）。
    private readonly object _learnLock = new();
    private LearnSession? _learn;

    /// <summary>学习会话是否进行中（锁内快照 —— 跨线程判 _learn 的统一入口，
    /// FilterBytes/心跳观测共用，不再各处手抄两行惯用法）。</summary>
    private bool IsLearning { get { lock (_learnLock) return _learn is not null; } }

    internal sealed class LearnSession
    {
        internal readonly DateTime Started = DateTime.Now;
        private byte[]? _press;                  // 按住中的报告（遇到 idle 帧结算成一次按键）
        internal string State = "waiting";       // waiting → counting → confirmed（换键重置回 waiting）
        internal ushort Usage;                   // counting/confirmed 的目标键
        internal ushort GotOther;                // 上次重置时实际收到的键（0=无；下个有效按键清零）
        internal int Count;                      // 已记录次数（同一键）
        internal bool SeenAny;                   // 学习会话收到过 DLL 上报（排障观测）
        internal ReportProfile? Profile;         // 第一组有效按键对推导出的指纹

        /// <summary>喂一份 DLL 上报的报告（全量流）。返回 = 状态是否变化（前端要刷新）。</summary>
        internal bool Feed(byte[] b)
        {
            // idle 判据：除 b[0]（ReportID）外全零 = 松开/空闲帧（Google/RC003 均如此）
            var idle = true;
            for (var i = 1; i < b.Length; i++) if (b[i] != 0) { idle = false; break; }
            if (!idle)
            {
                if (_press is null) _press = b;  // 按下沿（新的一次按压开始）
                return false;
            }
            if (_press is not { } press) return false;           // 没在按住：空闲帧忽略
            _press = null;
            if (State == "confirmed") return false;              // 已锁定：等前端处理
            var (p, u) = Diff(press, b);
            if (p is null) return false;                         // 不像遥控器报告（多键/异长），丢弃
            if (Profile is null) Profile = p;
            else if (p.W == 2 && Profile.W == 1 && p.Off == Profile.Off
                     && p.Len == Profile.Len && p.Id == Profile.Id)
                // 证据升级：本组按键的 diff 数据证明同槽是 16bit —— 推翻首键高字节
                // 为 0 时按下的单字节先验（usage<0x100 的键两种宽度数值相同，
                // 升级安全；升级后 16bit 键才提得出 0x0224 这类完整 usage）
                Profile = p;
            if (State == "waiting")
            {
                Usage = u; Count = 1; State = "counting";
                GotOther = 0;                            // 重置提示用过即清
                return true;
            }
            if (u != Usage)                              // counting 中换了键/干扰：
            {                                           // 重置不终止（2026-09-16 用户
                GotOther = u;                            // 定稿）—— 学习会话保留，
                State = "waiting";                       // 下一个有效按键成为新候选
                Count = 0;
                Usage = 0;
                return true;
            }
            if (++Count >= 3) State = "confirmed";
            return true;
        }

        /// <summary>confirmed → waiting：学下一个键（保存动作后由前端触发）。</summary>
        internal void Next()
        {
            if (State == "confirmed") { State = "waiting"; Count = 0; Usage = 0; }
        }

        /// <summary>按下/松开报告对 → (指纹, usage)。不像单键遥控器报告返回 (null, 0)。</summary>
        private static (ReportProfile? P, ushort U) Diff(byte[] press, byte[] idle)
        {
            if (press.Length != idle.Length || press.Length < 2) return (null, 0);
            if (press[0] != idle[0]) return (null, 0);          // ReportID 变了 = 两份来源不同
            int off = -1, w = 0;
            var firstDiff = -1;
            for (var i = 1; i < press.Length; i++)
            {
                if (press[i] == idle[i]) continue;
                if (firstDiff < 0) { firstDiff = i; continue; }
                if (w == 0 && i == firstDiff + 1) { w = 2; continue; }   // 相邻第二字节：16bit usage
                return (null, 0);          // >2 处不同或非相邻：键盘多键/鼠标，不像遥控器
            }
            if (firstDiff < 0) return (null, 0);
            if (w == 0)
            {
                off = firstDiff;
                // 1 字节不同的宽度歧义：小报告按 16bit 惯例（见区块头注释），大报告单字节
                w = press.Length - off >= 2 && press.Length <= 3 ? 2 : 1;
            }
            else off = firstDiff;
            var usage = (ushort)(w == 1 ? press[off] : (press[off] | (press[off + 1] << 8)));
            return usage == 0 ? (null, 0) : (new ReportProfile(press.Length, press[0], off, w), usage);
        }
    }

    /// <summary>跨线程下发 filter 的快照惯用法（先拷局部再判空，统一收在这里）。</summary>
    private void PushFilterToPipe()
    {
        // ★ 绝不在 HTTP 线程同步写：HTTP 是单线程串行 Loop，管道烂（对端不读、
        //   缓冲满）时 Write 永久阻塞 → 轮询请求堆积、连接池占满 → 全站按钮
        //   点不动（「开始设备学习」点了没反应就是这个）。后台写 + 超时；失败
        //   即断开管道，Session 的读循环立刻断线重连、重发 filter —— 自愈闭环。
        var pipe = _pipe;
        if (pipe is null) return;
        var bytes = FilterBytes();
        _ = Task.Run(async () =>
        {
            try { await WritePipeAsync(pipe, bytes); }
            catch { try { pipe.Dispose(); } catch { } }
        });
    }

    /// <summary>开始学习（幂等）：立即切 DLL 全量上报。返回当前快照。</summary>
    public JsonObject LearnStart()
    {
        lock (_learnLock) _learn ??= new LearnSession();
        PushFilterToPipe();
        return LearnSnapshot();
    }

    /// <summary>结束学习：清会话，DLL 回正常指纹过滤。</summary>
    public JsonObject LearnStop()
    {
        lock (_learnLock) _learn = null;
        PushFilterToPipe();
        return LearnSnapshot();
    }

    /// <summary>确认一键后继续学下一个（confirmed → waiting，前端保存动作后触发）。</summary>
    public JsonObject LearnNext()
    {
        lock (_learnLock) _learn?.Next();
        return LearnSnapshot();
    }

    /// <summary>当前学习会话已推导的指纹（保存学习成果的权威来源 —— 服务端推导
    /// 直接落库，不经前端回传；无会话/未推导返回 null）。</summary>
    public JsonObject? LearnProfile()
    {
        lock (_learnLock) return _learn?.Profile?.ToJson();
    }

    /// <summary>学习状态快照（前端轮询）：阶段 + 目标键 + 计数 + 推导指纹。</summary>
    public JsonObject LearnSnapshot()
    {
        lock (_learnLock)
        {
            var ls = _learn;
            if (ls is null) return new JsonObject { ["learning"] = false };
            return new JsonObject
            {
                ["learning"] = true,
                ["state"] = ls.State,               // waiting|counting|confirmed
                ["usage"] = $"0x{ls.Usage:X4}",     // counting/confirmed=目标键
                ["got"] = $"0x{ls.GotOther:X4}",    // 非 0=刚发生过重置（前端提示用）
                ["count"] = ls.Count,
                ["profile"] = ls.Profile?.ToJson(),
            };
        }
    }

    // ================= 映射执行器（v1 Player 语义 + v2 长短按双映射）=================
    //
    // 长短按判定（tap.dll 同时上报按下/空闲帧，间隔可测）：
    //   · 映射带可选 "long" 子对象时启用：按下后 0.4s 内松开 = 短按（发主配置）；
    //     按住 ≥0.4s = 长按（发 long 配置：hold 类持续、key 类每 0.3s 连发
    //     —— 「长按返回一直删除」就是 long={type:key,value:BACKSPACE}）；
    //   · 不带 long 子对象 → 行为与 v1 完全一致（key 立即发、hold 按住保持）。
    private sealed class Mapper
    {
        private const int LongPressMs = 400;
        private const int RepeatMs = 40;     // 长按连发间隔（2026-09-16 从 200 调整：
                                             // Windows 键盘自动重复标准 ≈30次/s(33ms)，
                                             // 200ms 只有 1/6，长按连删明显迟钝）

        private Dictionary<ushort, JsonObject> _map = new();
        private readonly Dictionary<ushort, PressState> _states = new();

        private sealed class PressState
        {
            public long PressedAt;
            public System.Threading.Timer? LongTimer;
            public bool LongActive;
            public System.Threading.Timer? Repeat;
        }

        /// <summary>映射命中回调（KeysRole.MappedFire 的转接：内部类不好碰外层事件）。</summary>
        internal Action? OnExecute;

        public void NoteStats(long total) { }

        public void Load(JsonObject keys)
        {
            // v2 结构：映射对象自带 usage（名字是纯标签）；v1 兼容：语义名反查
            var map = new Dictionary<ushort, JsonObject>();
            foreach (var (name, node) in keys)
            {
                if (node is not JsonObject m) continue;
                ushort? u = null;
                if (m["usage"]?.GetValue<string>() is { } us)
                {
                    us = us.Trim();
                    if (us.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) us = us[2..];
                    if (ushort.TryParse(us, System.Globalization.NumberStyles.HexNumber, null, out var uu))
                        u = uu;
                }
                else if (KeyUsage.TryGetValue(name, out var ku))
                    u = ku;
                if (u is { } usage)
                    map[usage] = m;
            }
            _map = map;
        }

        /// <summary>遥控器按下。</summary>
        public void Fire(ushort usage)
        {
            if (!_map.TryGetValue(usage, out var m)) return;
            var longMap = m["long"] as JsonObject;
            if (longMap is null)
            {
                Execute(usage, m, single: true);
                return;
            }
            // 有长按映射：延迟判定（0.4s 内松=短按；到点仍按=长按启动）
            var st = new PressState { PressedAt = Environment.TickCount64 };
            _states[usage] = st;
            st.LongTimer = new System.Threading.Timer(_ =>
            {
                st.LongActive = true;
                st.LongTimer = null;
                Execute(usage, longMap, single: false);
            }, null, LongPressMs, Timeout.Infinite);
        }

        /// <summary>遥控器松开（空闲帧=全部松开，逐键调这里）。</summary>
        public void Release(ushort usage)
        {
            var st = _states.Remove(usage, out var s) ? s : null;
            if (st is null)
            {
                // v1 兼容路径（无 long 映射）：抬起 hold 的
                Lift(usage, _map.GetValueOrDefault(usage));
                return;
            }
            st.LongTimer?.Dispose();
            st.Repeat?.Dispose();
            if (st.LongActive)
                Lift(usage, _map[usage]["long"]!.AsObject());       // 结束长按
            else if (Environment.TickCount64 - st.PressedAt < LongPressMs)
                Execute(usage, _map[usage], single: true);           // 短按
        }

        public void ReleaseAll()
        {
            foreach (var usage in _states.Keys.ToList())
                Release(usage);
            foreach (var usage in _map.Keys.ToList())
                Lift(usage, _map[usage]);
        }

        // ---- 动作执行 ----
        private void Execute(ushort usage, JsonObject m, bool single)
        {
            OnExecute?.Invoke();                        // 托盘按键动效
            var type = m["type"]?.GetValue<string>() ?? "";
            var value = m["value"]?.GetValue<string>() ?? "";
            var text = m["text"]?.GetValue<string>() ?? "";
            var mods = ModsWith(m);

            switch (type)
            {
                case "key" or "combo" when KeysInput.VkOf(value) is { } vk:
                    KeysInput.Combo(mods, vk);
                    if (!single)                       // 长按 key 类：连发（删除场景）
                        StartRepeat(usage, () => KeysInput.Combo(mods, vk));
                    break;
                case "hold" or "holdcombo" when KeysInput.VkOf(value) is { } vk2:
                    KeysInput.KeyDown2(mods, vk2);      // 按下保持（Lift 负责抬起）
                    break;
                case "mouse":
                    KeysInput.Mouse(value);
                    if (!single)
                        StartRepeat(usage, () => KeysInput.Mouse(value));
                    break;
                case "holdmouse":
                    if (value is "wheel_up" or "wheel_down")
                    {
                        KeysInput.Mouse(value);
                        StartRepeat(usage, () => KeysInput.Mouse(value), 60);
                    }
                    else
                        KeysInput.Mouse(value);         // 按住类鼠标键（Lift 发 *_up）
                    break;
                case "text" when text.Length > 0:
                    KeysInput.TypeText(text);
                    break;
            }
        }

        private void StartRepeat(ushort usage, Action act, int ms = RepeatMs)
        {
            if (_states.TryGetValue(usage, out var st))
            {
                st.Repeat?.Dispose();
                st.Repeat = new System.Threading.Timer(_ => act(), null, ms, ms);
            }
        }

        /// <summary>抬起/停止：hold 类键、按住类鼠标、连发定时器。</summary>
        private void Lift(ushort usage, JsonObject? m)
        {
            if (m is null) return;
            if (_states.TryGetValue(usage, out var st))
            {
                st.Repeat?.Dispose();
                st.Repeat = null;
            }
            var type = m["type"]?.GetValue<string>() ?? "";
            var value = m["value"]?.GetValue<string>() ?? "";
            if (type is "hold" or "holdcombo" && KeysInput.VkOf(value) is { } vk)
            {
                var mods = ModsWith(m);
                KeysInput.KeyUp(vk);
                for (int i = mods.Length - 1; i >= 0; i--) KeysInput.KeyUp(mods[i]);
            }
            else if (type == "holdmouse"
                     && value is not ("wheel_up" or "wheel_down"))
                KeysInput.Mouse(value + "_up");
        }

        private static ushort[] ModsWith(JsonObject m) =>
            (m["mods"] as JsonArray ?? new JsonArray())
            .Select(x => KeysInput.VkOf(x?.GetValue<string>())).Where(v => v.HasValue)
            .Select(v => v!.Value).ToArray();
    }
}

/// <summary>
/// 遥控器 HID 报告指纹：报告长度 + ReportID(b[0]) + usage 槽偏移/宽度（LE）。
/// 三个来源按优先级：用户学习结果（devices[addr].report）> 设备表预填 > Google 默认。
/// 实测参考：Google TV 遥控器 = (3, 0x02, 1, 2)「02 + usage LE16」；
/// 小米 RC003 = (9, 0x01, 3, 1)「键盘报告 01 … key1 在 b[3]」（axonkey 真机实测）。
/// </summary>
internal sealed record ReportProfile(int Len, int Id, int Off, int W)
{
    internal static readonly ReportProfile Google = new(3, 0x02, 1, 2);

    /// <summary>从 devices[addr] 节点读指纹；字段缺失/非法返回 null（走默认）。</summary>
    internal static ReportProfile? FromConfig(JsonObject? dev)
    {
        try
        {
            if (dev?["report"] is not JsonObject r) return null;
            var p = new ReportProfile(
                r["len"]!.GetValue<int>(), r["id"]!.GetValue<int>(),
                r["off"]!.GetValue<int>(), r["w"]!.GetValue<int>());
            return p.Len is >= 1 and <= 64 && p.W is 1 or 2
                   && p.Off >= 0 && p.Off + p.W <= p.Len ? p : null;
        }
        catch { return null; }
    }

    internal JsonObject ToJson() =>
        new() { ["len"] = Len, ["id"] = Id, ["off"] = Off, ["w"] = W };
}
