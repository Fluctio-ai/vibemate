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
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private readonly Mapper _mapper = new();

    public volatile bool Ready;
    public volatile string Note = "未启动";
    /// <summary>按键事件（学习模式监听用）：usage + 按下/松开。</summary>
    public event Action<ushort, bool>? KeyEvent;
    /// <summary>映射命中并已执行 —— 托盘按键动效用。语音键未改映射时映射表里
    /// 没有它（Mapper.Fire 早退），自然不触发，无需任何特判。</summary>
    public event Action? MappedFire;
    /// <summary>遥控器有活动 —— 语音角色用它立刻重连（遥控器只在按键后醒一小会）。</summary>
    public event Action? Activity;

    public KeysRole(Func<string, Task> log, Func<JsonObject> getKeys)
    {
        _log = log;
        _getKeys = getKeys;
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
            var pid = FindWudfHostPid();
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
        // 0) DLL 可能已在（重启主程序/上轮注入）：先快速试连。
        //    ★ 成功判据 = 管道连得上 —— WaitNamedPipe 的 0 超时语义不可靠，
        //    直接以连接为准，一网打尽所有边缘情况。
        var pipe = await TryConnect(pid, 500, ct);
        if (pipe is null)
        {
            // 1) 确保注入（内部处理管理员/普通权限两条路）
            if (!await EnsureInjected(pid))
            {
                Note = "注入失败（UAC 被拒？）";
                return;
            }
            pipe = await TryConnect(pid, 10000, ct);
        }
        if (pipe is null)
        {
            Note = "注入完成但连不上管道";
            await _log("按键：注入后 10s 内连不上管道");
            return;
        }
        using var _ = pipe;
        Note = "已连接";
        Ready = true;
        await _log($"按键：管道已连接（WUDFHost {pid}）");

        // 下发当前映射的屏蔽表
        SendBlock(pipe);

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

    private static async Task<NamedPipeClientStream?> TryConnect(
        int pid, int timeoutMs, CancellationToken ct)
    {
        // ★ 管道名 vibemote-keys-<pid> 是 tap.dll 里写死的契约（DLL 侧创建同名
        //   管道）—— 项目改名 VibeMate 也不动它，动了就再也连不上
        var s = new NamedPipeClientStream(".", $"vibemote-keys-{pid}", PipeDirection.InOut);
        try { await s.ConnectAsync(timeoutMs, ct); return s; }
        catch { s.Dispose(); return null; }
    }

    private void HandleLine(NamedPipeClientStream pipe, string line)
    {
        if (line.StartsWith("report "))
        {
            var parts = line[7..].Split(' ');
            if (parts.Length == 3
                && byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var b0)
                && byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var b1)
                && byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var b2)
                && b0 == 0x02)
            {
                Activity?.Invoke();
                var usage = (ushort)(b1 | (b2 << 8));
                if (usage == 0)
                    _mapper.ReleaseAll();                    // 空闲帧：全部松开
                else
                {
                    KeyEvent?.Invoke(usage, true);
                    _mapper.Fire(usage);
                }
            }
        }
        else if (line.StartsWith("hb "))
        {
            var p = line[3..].Split(' ');
            if (p.Length == 3 && long.TryParse(p[0], out var total))
                _mapper.NoteStats(total);
        }
    }

    private void SendBlock(NamedPipeClientStream pipe)
    {
        var keys = _getKeys();
        var us = keys.Where(k => KeyUsage.ContainsKey(k.Key))
                     .Select(k => KeyUsage[k.Key])
                     .Select(u => u.ToString("x4"));
        var cmd = Encoding.ASCII.GetBytes("block " + string.Join(' ', us) + "\n");
        try { pipe.Write(cmd, 0, cmd.Length); } catch { /* 连接将断，主循环兜 */ }
    }

    // ================= WUDFHost 定位（v1 keys.py:351 注册表法）=================
    private static int? FindWudfHostPid(string vid = "18d1", string pid = "9450")
    {
        const string svcPrefix = "{00001812-0000-1000-8000-00805f9b34fb}";
        using var root = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice");
        if (root is null) return null;
        foreach (var svcName in root.GetSubKeyNames())
        {
            var low = svcName.ToLowerInvariant();
            if (!low.StartsWith(svcPrefix) || !low.Contains(vid) || !low.Contains(pid))
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
        if (!Program.ExtractResource("VibeMate.tap.dll", dll))
        {
            // 写失败也可能是 tap.dll 已被 WUDFHost 加载锁定（注入其实成功了）→ 权威判据 = 模块枚举
            if (!IsModuleLoaded(pid, "tap.dll"))
            {
                Note = "缺少内嵌 tap.dll";
                await _log("按键：tap.dll 资源释放失败");
                return false;
            }
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
            var pid = FindWudfHostPid();
            if (pid is null) return;
            var pipe = new NamedPipeClientStream(".", $"vibemote-keys-{pid}", PipeDirection.InOut);
            await pipe.ConnectAsync(1000);
            var cmd = Encoding.ASCII.GetBytes("unload\n");
            await pipe.WriteAsync(cmd);
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
        private const int RepeatMs = 200;    // 长按连发间隔（用户定稿 0.2s）

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
