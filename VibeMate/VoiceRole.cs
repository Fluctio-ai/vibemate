using System.Text.Json.Nodes;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace VibeMate;

/// <summary>
/// 语音角色（里程碑③骨架）：WinRT GATT 直连遥控器 → ATVV 服务 → 状态特征。
///
/// 对齐 v1 voice.py 的架构纪律：
///   · 独立线程 + 异步循环，长连接不占主线程；
///   · 断开/失败按指数退避重连（最长 45s —— v1 实测值，避免疯狂刷日志）；
///   · 「枚举到 4 个基础服务、ATVV 消失」= 会话烂掉：这里先记日志计数，
///     里程碑④再把「Dispose + Uncached 重建」的自愈接进来；
///   · 音频管线（ADPCM→高通→重采样→AGC→限幅→WASAPI）下一轮接入，
///     本轮先打通「按住语音键 → 事件」这一层。
/// </summary>
public sealed class VoiceRole : IDisposable
{
    // ATVV 特征（v1 voice.py:30 —— 前缀匹配，128 位自定义 UUID 以此开头）
    private const string SvcPrefix = "ab5e0001";
    private const string CtlPrefix = "ab5e0002";     // 写：GET_CAPS
    private const string AudioPrefix = "ab5e0003";   // 通知：16k IMA ADPCM
    private const string StatusPrefix = "ab5e0004";  // 通知：0x04 按住 / 0x00 松开

    private readonly Func<string, Task> _log;
    private readonly Func<ulong?> _getAddr;
    private readonly Func<JsonNode?> _getVoiceSpec;
    private readonly Func<JsonNode?> _getAudioSpec;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private VoiceKey _voiceKey;
    private MicRelay? _relay;

    // 音频链路（v1 _audio_supervisor 的看护模式）
    private readonly AudioSink _sink = new();
    private AudioPipeline? _pipeline;
    private Task? _audioWatch;
    private long _frames;                       // 本进程累计音频帧
    private long _sessionFrames;

    public volatile bool Connected;
    public volatile string Note = "未启动";
    /// <summary>最近一次服务表（普适性：按能力装配，服务表=能力清单）。</summary>
    public volatile string[] Services = Array.Empty<string>();
    public long Frames => _frames;

    public VoiceRole(Func<string, Task> log, Func<ulong?> getAddr,
                     Func<JsonNode?> getVoiceSpec, Func<JsonNode?>? getAudioSpec = null)
    {
        _log = log;
        _getAddr = getAddr;
        _getVoiceSpec = getVoiceSpec;
        _getAudioSpec = getAudioSpec ?? (() => null);
        _voiceKey = new VoiceKey(getVoiceSpec());
        if (_voiceKey.ClearStuck())
            _ = log("语音键：清掉上次残留的按下状态");
    }

    /// <summary>当前 audio 设置（gain/agc/relay，v1 默认值兜底）。</summary>
    private (float gain, bool agc, bool relay) ReadAudio()
    {
        var a = _getAudioSpec() as JsonObject;
        return ((float?)a?["gain"] ?? 4.0f,
                a?["agc"]?.GetValue<bool>() ?? true,
                a?["relay"]?.GetValue<bool>() ?? true);
    }

    /// <summary>语音键主键 VK（MicRelay 判「按住键盘快捷键」用）。</summary>
    private ushort? HotkeyVk() => _voiceKey.Ok ? _voiceKey.KeyVk : null;

    /// <summary>audio 设置热更新（ConfigChanged 即时生效）。</summary>
    public void ApplyAudio()
    {
        var (gain, agc, relay) = ReadAudio();
        if (_pipeline is not null)
        {
            _pipeline.UseAgc = agc;
            _pipeline.FixedGain = gain;
        }
        if (!relay && _relay is not null)
        {
            _relay.Dispose();
            _relay = null;
            _ = _log("语音：内置麦克风直通已关闭");
        }
        // 开启侧由 AudioSupervisor 下一轮（5s 内）自然接上
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(Run) { IsBackground = true, Name = "VoiceRole" };
        _thread.Start();
    }

    private void Run() => _ = MainLoop(_cts!.Token);

    private async Task MainLoop(CancellationToken ct)
    {
        await _log("语音角色：启动（等待连接循环）");
        _audioWatch = Task.Run(() => AudioSupervisor(ct));
        var fails = 0;

        while (!ct.IsCancellationRequested)
        {
            var addr = _getAddr();
            if (addr is null)
            {
                Note = "未配置遥控器地址";
                await SleepSafe(ct, 5000);
                continue;
            }

            // 会话开始时按当前配置重建语音键（热更新：改完下一轮会话生效）
            _voiceKey = new VoiceKey(_getVoiceSpec());

            try
            {
                var ok = await TrySession(addr.Value, ct);
                if (ok) fails = 0;
                else fails++;
            }
            catch (Exception e)
            {
                fails++;
                await _log($"语音：会话异常 {e.GetType().Name}: {e.Message}");
            }

            if (ct.IsCancellationRequested) break;

            // v1 退避纪律：1s 起指数翻倍封顶 45s；但 poke（遥控器按键活动）
            // 随时打断 —— 它只在醒着的一小段窗口内能被连上
            var backoff = Math.Min(45000, 1000 << Math.Min(fails, 6));
            Note = fails >= 3
                ? $"连不上（第 {fails} 次，{backoff / 1000}s 后重试）"
                : "未连上（遥控器休眠？按一下任意键会立刻重试）";
            await _log($"语音：会话结束，{backoff / 1000}s 后重试（可被按键活动打断）");
            var woke = false;
            for (int waited = 0; waited < backoff; waited += 100)
            {
                if (ct.IsCancellationRequested) break;
                if (_poke.IsSet) { _poke.Reset(); woke = true; break; }
                await Task.Delay(100, ct).ContinueWith(_ => { });
            }
            if (woke) await _log("语音：检测到遥控器活动，立刻重试连接");
        }
        await _log("语音角色：退出");
    }

    /// <summary>
    /// 音频出口看护（v1 _audio_supervisor 一比一）：出口死了/没找到就每 5s 重试。
    /// 开机自启时虚拟声卡常比程序晚就绪，只在启动时找一次的话找不到就永远没声音。
    /// </summary>
    private async Task AudioSupervisor(CancellationToken ct)
    {
        bool announced = false;
        while (!ct.IsCancellationRequested)
        {
            if (!_sink.Alive)
            {
                if (_sink.Start())
                {
                    await _log($"语音：音频出口已开启（{_sink.DeviceName}）");
                    announced = false;
                }
                else if (!announced)
                {
                    announced = true;
                    await _log("语音：暂时找不到虚拟声卡 VB-CABLE —— 每 5 秒重试"
                               + "（开机后声卡常比程序晚就绪；管理员下会自动静默安装，"
                               + "或到界面语音页手动安装）");
                }
            }

            // 内置麦克风直通（v1 语义：开着就常驻看护，按住语音快捷键才占麦克风）
            var (_, _, relay) = ReadAudio();
            if (relay && _sink.Alive && _relay is null)
            {
                _relay = new MicRelay(_sink, HotkeyVk, _log);
                _relay.Start();
                await _log("语音：麦克风直通看护已启动（按住键盘语音快捷键时转发）");
            }
            await Task.Delay(5000, ct);
        }
    }

    /// <summary>一次完整会话：连接 → 找 ATVV → 订阅状态 → 挂到断开。返回是否成功建立。</summary>
    private async Task<bool> TrySession(ulong addr, CancellationToken ct)
    {
        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
        if (dev is null)
        {
            await _log("语音：FromBluetoothAddressAsync 返回空（地址不存在/没配对）");
            return false;
        }
        if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            // 触发连接的官方姿势：读一遍服务表（GetGattServicesAsync 会建立链路）
            await Task.Delay(300, ct);
        }
        await _log($"语音：设备对象已建（{dev.ConnectionStatus}），枚举服务…");

        var svcResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (svcResult.Status != GattCommunicationStatus.Success)
        {
            await _log($"语音：枚举服务失败（{svcResult.Status}）—— 遥控器多半在休眠");
            return false;
        }

        // 普适性核心：服务表=设备能力清单。ATVV 只是「Google 系才有的语音能力」：
        // · 只有少量基础服务（烂会话）→ 重连可治（里程碑④做自愈）
        // · 完整服务表但没有 ab5e0001 → 该设备就是没有语音能力（其他遥控器），
        //   不是错误：标注清楚，按键映射等其他能力照常可用
        var svcList = svcResult.Services.ToList();
        var names = svcList.Select(s => s.Uuid.ToString().ToLower()[..8]).ToArray();
        Services = names;
        await _log($"语音：枚举到 {names.Length} 个服务：{string.Join(", ", names)}");

        var svc = svcList.FirstOrDefault(
            s => s.Uuid.ToString().ToLower().StartsWith(SvcPrefix));
        if (svc is null)
        {
            if (names.Length <= 5)
            {
                // ★ v2 自愈点：烂会话（残缺服务表）。每轮都是新的
                //   BluetoothLEDevice + Uncached 枚举，本身就等价于「Dispose +
                //   重建」；这里只负责计数上报，3 次后请托盘气泡提示用户。
                _badSessions++;
                BadSession?.Invoke(_badSessions);
                await _log($"语音：服务表残缺（{names.Length} 个）—— 会话烂了，"
                           + $"第 {_badSessions} 次；已自动用 Uncached 重建");
            }
            else
            {
                _badSessions = 0;
                Note = "此设备无语音能力（不带 ATVV）";
                await _log("语音：该遥控器不带 ATVV 语音服务 —— 语音角色静默，按键能力不受影响");
                await SleepSafe(ct, 30000);   // 半分钟后再看一眼（设备可能被换）
            }
            foreach (var s in svcList) s.Dispose();
            return false;
        }
        _badSessions = 0;

        var charsResult = await svc.GetCharacteristicsAsync();
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            await _log($"语音：枚举特征失败（{charsResult.Status}）");
            svc.Dispose();
            return false;
        }
        GattCharacteristic? Find(string prefix) => charsResult.Characteristics.FirstOrDefault(
            c => c.Uuid.ToString().ToLower().StartsWith(prefix));

        var status = Find(StatusPrefix);
        var ctl = Find(CtlPrefix);
        var audio = Find(AudioPrefix);
        if (status is null || ctl is null || audio is null)
        {
            await _log("语音：ATVV 特征不全（需要 0002 控制、0003 音频与 0004 状态）");
            svc.Dispose();
            return false;
        }

        // 订阅状态特征：语音键按下/松开 → VoiceKey 合成输入法快捷键
        status.ValueChanged += OnStatus;
        if (!await Subscribe(status, "状态"))
        {
            status.ValueChanged -= OnStatus;
            svc.Dispose();
            return false;
        }

        // 订阅音频特征：16kHz IMA ADPCM，128B 一帧 → 管线 → 虚拟声卡
        audio.ValueChanged += OnAudio;
        if (!await Subscribe(audio, "音频"))
        {
            status.ValueChanged -= OnStatus;
            audio.ValueChanged -= OnAudio;
            svc.Dispose();
            return false;
        }

        // GET_CAPS：让遥控器进入录音态（v1 voice.py:1073 的字节，无响应写）
        try
        {
            using var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(new byte[] { 0x0A, 0x00, 0x04, 0x00, 0x01 });
            await ctl.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        }
        catch (Exception e)
        {
            await _log($"语音：GET_CAPS 发送失败（不影响收流）：{e.Message}");
        }

        Connected = true;
        Note = "已连接" + (_voiceKey.Ok ? $"（语音键 → {_voiceKey.Label}）" : "（语音键目标无效）");
        _pipeline = new AudioPipeline();               // 会话开始：滤波器状态清零
        var (gain, agc, _) = ReadAudio();              // v1 audio 设置即刻应用
        _pipeline.UseAgc = agc;
        _pipeline.FixedGain = gain;
        _sessionFrames = 0;
        await _log("语音：已连接，ATVV 状态+音频特征已订阅");
        _lastFrame = Environment.TickCount64;

        // 挂着：等设备断开（Watch 状态）或本端取消。10s 无任何动静也继续挂——
        // 语音键不按的时候特征就是静默的，不能当超时依据。
        var tcs = new TaskCompletionSource();
        TypedEventHandler<BluetoothLEDevice, object> onConn = (d, _) =>
        {
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                tcs.TrySetResult();
        };
        dev.ConnectionStatusChanged += onConn;
        using var reg = ct.Register(() => tcs.TrySetResult());
        await tcs.Task;

        dev.ConnectionStatusChanged -= onConn;
        status.ValueChanged -= OnStatus;
        audio.ValueChanged -= OnAudio;
        Connected = false;
        Note = "连接断开";
        await _log($"语音：连接断开（本会话音频 {_sessionFrames} 帧）");
        svc.Dispose();
        return true;
    }

    /// <summary>音频帧到达：128B ADPCM → 五级管线 → 虚拟声卡。</summary>
    private void OnAudio(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buf = args.CharacteristicValue;
        if (buf.Length == 0 || _pipeline is null) return;
        var frame = new byte[buf.Length];
        using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(buf))
            dr.ReadBytes(frame);
        _frames++;
        _sessionFrames++;
        var pcm = _pipeline.Feed(frame);
        _sink.Feed(pcm);
        // 每 2 秒一条流水（125 帧 × 16ms ≈ 2s）：证明音频在流，不刷屏
        if (_sessionFrames % 125 == 1 && _sessionFrames > 1)
            _ = _log($"语音：音频流中（本会话 {_sessionFrames} 帧）");
    }

    /// <summary>
    /// 按特征自身属性选 Notify 或 Indicate 写 CCCD。
    /// ★ 只支持 Indicate 的特征写 Notify 的 CCCD 不报错、但永远不推数据 ——
    ///   这正是「订阅成功却一帧音频都没有」的元凶（bleak 的 start_notify
    ///   内部会自动二选一，WinRT 要自己来）。
    /// </summary>
    private async Task<bool> Subscribe(GattCharacteristic c, string what)
    {
        var props = c.CharacteristicProperties;
        var mode = (props & GattCharacteristicProperties.Notify) != 0
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : (props & GattCharacteristicProperties.Indicate) != 0
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : 0;
        if (mode == 0)
        {
            await _log($"语音：{what}特征既不支持 Notify 也不支持 Indicate");
            return false;
        }
        var r = await c.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
        if (r != GattCommunicationStatus.Success)
        {
            await _log($"语音：订阅{what}特征失败（{r}）");
            return false;
        }
        if (mode == GattClientCharacteristicConfigurationDescriptorValue.Indicate)
            await _log($"语音：{what}特征走 Indicate 模式");
        return true;
    }

    private long _lastFrame;
    private int _badSessions;

    private void OnStatus(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buf = args.CharacteristicValue;
        if (buf.Length == 0) return;
        var b = new byte[buf.Length];
        using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(buf))
            dr.ReadBytes(b);
        _lastFrame = Environment.TickCount64;
        var pressed = b[0] == 0x04;
        VoiceKeyEvent?.Invoke(pressed);
        if (VoiceKeyEnabled)
        {
            // 声浪动效只挂在真实语音链路上：语音键被改映射成普通键时这里不发，
            // 该键走 MappedFire 的按键波纹 —— 信号在正确层产生，下游无需特判
            Speaking?.Invoke(pressed);
            _voiceKey.Fire(pressed);      // hold：按住期间保持按下；tap：完整点击
        }
        else
            _ = _log(pressed ? "语音：▼ 按住语音键（已映射为普通键，语音功能停用）"
                             : "语音：▲ 松开语音键");
    }

    private static async Task SleepSafe(CancellationToken ct, int ms)
    {
        try { await Task.Delay(ms, ct); }
        catch (TaskCanceledException) { }
    }

    public void ReloadKey()
    {
        // 改语音键目标即时生效（不等下一轮会话）：先补抬起旧键再换新
        _voiceKey.Release();
        _voiceKey = new VoiceKey(_getVoiceSpec());
    }

    private readonly ManualResetEventSlim _poke = new(false);

    /// <summary>语音键按下/松开（转发给按键系统做虚拟键映射；见 KeysRole.voice）。</summary>
    public event Action<bool>? VoiceKeyEvent;
    /// <summary>语音输入进行中变化（按住语音键=true）—— 托盘声波动效用。</summary>
    public event Action<bool>? Speaking;
    /// <summary>VoiceKey 是否启用：语音键被映射成普通键时置 false（互斥，避免双发）。</summary>
    public volatile bool VoiceKeyEnabled = true;

    /// <summary>遥控器刚有按键活动 —— 现在它醒着，立刻重试连接（v1 poke 机制：
    /// 遥控器只在刚醒的一小段时间能被连上，等退避睡满就错过窗口了）。</summary>
    public void Poke() => _poke.Set();

    /// <summary>蓝牙会话残缺（烂会话）次数变化；n≥3 建议用户干预（托盘气泡用）。</summary>
    public event Action<int>? BadSession;

    public void Dispose()
    {
        // 幂等：托盘「重启」会先 Dispose 释放 BLE 会话（单客户端铁律），
        // Main 收尾还会再进一次 —— 重复 Cancel 已释放的 cts 会抛 ObjectDisposed
        var cts = _cts; _cts = null;
        cts?.Cancel();
        try { _thread?.Join(3000); } catch { }
        _thread = null;
        _relay?.Dispose();
        _relay = null;
        _sink.Stop();
        cts?.Dispose();
    }
}
