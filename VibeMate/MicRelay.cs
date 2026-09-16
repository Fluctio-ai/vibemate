using NAudio.Wave;

namespace VibeMate;

/// <summary>
/// 内置麦克风直通（v1 MicRelay 一比一）：真实麦克风 → 同一个 CABLE 出口，
/// 在「按住键盘语音快捷键」时转发 —— 输入法把麦克风钉死在 CABLE 上时，
/// 平时用键盘触发语音输入法也能有声音。
///
/// ★ 两条 v1 铁律：
///   · 默认录音设备若是 CABLE Output（虚拟声卡自己）绝不读 —— 否则把声卡输出
///     再灌回它自己输入，人声叠一层 ~115ms 延迟副本，识别率明显下降；
///   · 只在快捷键按住期间转发（平时不占麦克风，指示条不亮）。
/// </summary>
internal sealed class MicRelay : IDisposable
{
    private readonly AudioSink _sink;
    private readonly Func<ushort?> _hotkey;       // 语音键主键 VK（按住 = 转发窗口）
    private readonly Func<string, Task> _log;
    private WaveInEvent? _waveIn;
    private Thread? _watch;
    private volatile bool _running;
    private volatile bool _active;

    public bool Running => _running;

    public MicRelay(AudioSink sink, Func<ushort?> hotkey, Func<string, Task> log)
    {
        _sink = sink;
        _hotkey = hotkey;
        _log = log;
    }

    /// <summary>启动看护（不占麦克风；按住快捷键时才开采集）。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _watch = new Thread(WatchLoop) { IsBackground = true, Name = "micrelay" };
        _watch.Start();
    }

    private async void WatchLoop()
    {
        var warned = false;
        while (_running)
        {
            try
            {
                var vk = _hotkey();
                var down = vk is { } v && KeysInput.IsDown(v);
                if (down && !_active && _sink.Alive)
                {
                    if (TryOpenMic())
                        _active = true;
                    else if (!warned)
                    {
                        warned = true;
                        await _log("直通：默认录音设备是虚拟声卡（或不可用），"
                                   + "不转发 —— 把系统默认录音设备改回真实麦克风即可");
                    }
                }
                else if (!down && _active)
                {
                    StopMic();
                    _active = false;
                }
            }
            catch { /* 单帧错误忽略 */ }
            Thread.Sleep(50);
        }
        StopMic();
    }

    /// <summary>开采集；默认设备是 CABLE/回环 → false（防自环铁律）。</summary>
    private bool TryOpenMic()
    {
        try
        {
            var cap = WaveIn.GetCapabilities(0);          // 0 = 系统默认录音设备
            var n = cap.ProductName.ToLowerInvariant();
            if (n.Contains("cable") || n.Contains("vb-audio")) return false;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 3,
            };
            _waveIn.DataAvailable += (_, e) =>
            {
                if (!_active || e.BytesRecorded == 0) return;
                // 单声道 → 左右同相复制（对齐管线的立体声形态）
                var stereo = new byte[e.BytesRecorded * 2];
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    stereo[i * 2] = e.Buffer[i];
                    stereo[i * 2 + 1] = e.Buffer[i + 1];
                    stereo[i * 2 + 2] = e.Buffer[i];
                    stereo[i * 2 + 3] = e.Buffer[i + 1];
                }
                _sink.FeedRaw(stereo);
            };
            _waveIn.StartRecording();
            return true;
        }
        catch
        {
            StopMic();
            return false;
        }
    }

    private void StopMic()
    {
        try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
        _waveIn = null;
    }

    public void Dispose()
    {
        _running = false;
        try { _watch?.Join(1000); } catch { }
        StopMic();
    }
}
