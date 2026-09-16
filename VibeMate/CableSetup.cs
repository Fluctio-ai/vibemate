using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VibeMate;

/// <summary>
/// VB-CABLE 虚拟声卡的静默安装（2026-09-16 需求：安装包自带驱动，没装就自动装）。
///
/// 组成：驱动三件套（inf/cat/sys）+ 官方 Setup（兜底用）全部嵌在 exe 资源里
///（LogicalName 前缀 VibeMate.vbcable.*，见 csproj），安装时解压到
/// ProgramData\VibeMate\vbcable 再走系统工具 —— exe 旁不落任何文件。
///
/// 安装路径（要求管理员 —— v2 主程序本就跑在最高权限计划任务里）：
///   1. pnputil /add-driver xxx.inf /install —— 驱动入包库（.cat 签名校验在此发生）；
///   2. SwDeviceCreate 造出 root 软件设备（硬件 ID=VBAudioVACWDM，INF 里写死的）——
///      ★ pnputil 只负责「备货」，设备节点必须另造，否则永远没有 CABLE Input 端点；
///   3. 轮询端点确认真的出现（PnP 装驱动是异步的，有几秒延迟）。
/// 全程失败 → 返回 false；界面语音页留有手动按钮，解压目录里也有官方 Setup 兜底。
/// </summary>
internal static class CableSetup
{
    private const string ResPrefix = "VibeMate.vbcable.";
    // inf/cat/sys 必须同目录（pnputil 按包解析）；官方 Setup 是兜底件
    private static readonly string[] Files =
    {
        "vbMmeCable64_win10.inf",
        "vbaudio_cable64_win10.cat",
        "vbaudio_cable64_win10.sys",
        "VBCABLE_Setup_x64.exe",
    };

    private static bool _cached;
    private static DateTime _checkedUtc = DateTime.MinValue;
    private static int _installing;

    /// <summary>有没有可用的 CABLE 播放端点（判定与 AudioSink 共用 FindCableDevice）。
    /// ★ 缓存不对称：正结果粘 60s（装好后几乎永不变，而 stateBuilder 每 2s 一问，
    ///   没必要为它反复开 COM 枚举）；负结果 5s（未装时短周期重查，装完立刻翻正）。</summary>
    public static bool Installed()
    {
        if ((DateTime.UtcNow - _checkedUtc).TotalSeconds < (_cached ? 60 : 5)) return _cached;
        _checkedUtc = DateTime.UtcNow;
        try
        {
            using var dev = AudioSink.FindCableDevice();
            _cached = dev is not null;
        }
        catch { _cached = false; }
        return _cached;
    }

    private static void Invalidate() => _checkedUtc = DateTime.MinValue;

    private static string DriverDir => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.CommonApplicationData), "VibeMate", "vbcable");

    /// <summary>安装（要求管理员；普通权限走 runas 自身 --install-cable）。
    /// 并发防护：开机自动装 + 界面手动装可能撞车，同时只放一个进来。</summary>
    public static async Task<(bool Ok, string Msg)> InstallAsync(Func<string, Task> log)
    {
        if (Interlocked.Exchange(ref _installing, 1) != 0)
            return (true, "安装已在进行中");
        try { return await InstallCore(log); }
        finally { _installing = 0; }
    }

    private static async Task<(bool Ok, string Msg)> InstallCore(Func<string, Task> log)
    {
        Invalidate();
        if (Installed()) return (true, "已安装");

        // 1) 解压驱动四件套（ProgramData：服务账户可读；覆盖写 —— 版本可能更新）
        try { Directory.CreateDirectory(DriverDir); }
        catch (Exception e) { return (false, $"解压失败：{e.Message}"); }
        var missing = Files.FirstOrDefault(
            f => !Program.ExtractResource(ResPrefix + f, Path.Combine(DriverDir, f)));
        if (missing is not null) return (false, $"缺少内嵌资源 {ResPrefix}{missing}");
        await log($"驱动文件已解压到 {DriverDir}");

        // 2) pnputil 入库（+顺手装到已存在的设备）
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil",
                Arguments = $"/add-driver \"{Path.Combine(DriverDir, "vbMmeCable64_win10.inf")}\" /install",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();   // 先读完再等退出：防管道写满死锁
            await p.WaitForExitAsync();
            await log($"pnputil 退出码 {p.ExitCode}：{output.ReplaceLineEndings(" ").Trim()}");
        }
        catch (Exception e) { return (false, $"pnputil 启动失败：{e.Message}"); }

        Invalidate();
        if (Installed()) return (true, "驱动已就绪（设备端点已在）");

        // 3) 造 root 软件设备 —— 没有它 PnP 永远匹配不到这个 INF（见 SoftwareDevice 注释）
        if (!SoftwareDevice.Create("VBAudioVACWDM", "VB-Audio Virtual Cable",
                                   m => { _ = log(m); }))
            return (false, "软件设备创建失败（见 [CABLE] 日志；可手动运行解压目录的官方 Setup）");

        // 4) PnP 异步装驱动：轮询端点出现（最长 20s）
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            Invalidate();
            if (Installed())
            {
                await log("CABLE Input 端点已出现");
                return (true, "安装完成");
            }
        }
        return (false, "驱动已入库但端点 20s 内没出现 —— 建议重启后再看，或手动运行官方 Setup");
    }

    /// <summary>
    /// cfgmgr32 软件设备 API（SwDeviceCreate）：造 root 虚拟设备的正路 ——
    /// pnputil 只入库不建节点。官方 VBCABLE_Setup 也是这么造设备的。
    /// 结构体布局对齐 swdevice.h；Lifetime 字段自 Win8 就有，目标平台 win10+
    /// 不存在无该字段的 36 字节老布局 —— cbSize 直接取 Marshal.SizeOf（与结构体
    /// 自洽，将来加字段也不会漂移）。
    /// </summary>
    internal static class SoftwareDevice
    {
        private const uint SW_DEVICE_Lifetime_Persistent = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SW_DEVICE_CREATE_INFO
        {
            public uint cbSize;
            public string InstanceId;
            public string DeviceDescription;
            public IntPtr ContainerId;       // const GUID*；NULL = 系统自动分容器
            public uint DefaultContainer;    // DEV_BOOLEAN
            public uint Lifetime;            // SW_DEVICE_LIFETIME
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY { public Guid FmtId; public uint Pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPCOMP
        {
            public IntPtr Key;               // DEVPROPKEY*
            public uint Type;
            public uint BufferSize;
            public IntPtr Buffer;
        }

        private delegate void CreateCallback(IntPtr h, int hr, IntPtr ctx,
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SwDeviceCreate(string enumeratorName, string? parentDeviceId,
            ref SW_DEVICE_CREATE_INFO info, uint cPropertyCount, DEVPROPCOMP[] properties,
            CreateCallback callback, IntPtr context, out IntPtr hSwDevice);

        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern int SwDeviceClose(IntPtr hSwDevice);

        // DEVPKEY_Device_HardwareIds（devpkey.h：fmtid a45c254e-…，pid 3）
        private static readonly DEVPROPKEY KeyHardwareIds = new()
        { FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 3 };
        private const uint DEVPROP_TYPE_STRING_LIST = 0x2013;   // REG_MULTI_SZ 形态

        // 回调可能从别的线程进来：钉成静态防 GC，结果用 TCS 取回（不自旋）
        private static readonly TaskCompletionSource _done = new();
        private static CreateCallback? _cb;
        private static int _hr = -1;
        private static string _created = "";

        /// <summary>创建 root 软件设备（硬件 ID=hardwareId，持久存在）。true=创建成功。</summary>
        public static bool Create(string hardwareId, string description, Action<string> log)
        {
            var ids = (hardwareId + "\0\0").ToCharArray();   // MULTI_SZ："id\0" + 列表终止"\0"
            var hkey = KeyHardwareIds;                       // 要取地址：钉住局部副本防搬移
            var prop = new DEVPROPCOMP[1];
            var hIds = GCHandle.Alloc(ids, GCHandleType.Pinned);
            prop[0].Key = Marshal.AllocHGlobal(Marshal.SizeOf<DEVPROPKEY>());
            try
            {
                Marshal.StructureToPtr(hkey, prop[0].Key, false);
                prop[0].Type = DEVPROP_TYPE_STRING_LIST;
                prop[0].BufferSize = (uint)(ids.Length * 2);
                prop[0].Buffer = hIds.AddrOfPinnedObject();

                _cb = Callback;
                var info = new SW_DEVICE_CREATE_INFO
                {
                    cbSize = (uint)Marshal.SizeOf<SW_DEVICE_CREATE_INFO>(),
                    InstanceId = "0000",
                    DeviceDescription = description,
                    ContainerId = IntPtr.Zero,
                    DefaultContainer = 0,
                    Lifetime = SW_DEVICE_Lifetime_Persistent,
                };
                var rc = SwDeviceCreate("VBAudioVACWDM", null, ref info, 1, prop, _cb,
                                        IntPtr.Zero, out var h);
                if (rc != 0 || h == IntPtr.Zero)
                {
                    log($"SwDeviceCreate 失败 rc=0x{rc:x8}");
                    return false;
                }
                _done.Task.Wait(3000);            // 等回调带回设备实例 ID（TCS，不自旋空转）
                SwDeviceClose(h);                 // Persistent：关句柄设备仍在
                log($"软件设备已创建：{_created} hr=0x{_hr:x8}");
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(prop[0].Key);
                hIds.Free();
            }
        }

        private static void Callback(IntPtr h, int hr, IntPtr ctx, string deviceId)
        {
            _hr = hr;
            _created = deviceId ?? "";
            _done.TrySetResult();
        }
    }
}
