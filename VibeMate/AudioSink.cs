using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VibeMate;

/// <summary>
/// 音频出口 —— 把管线输出的 PCM 写进 VB-CABLE 的输入端点（CABLE Input，
/// 即“播放设备”视角），对齐 v1 Sink 的行为：
///
///   · 找不到 CABLE → 不抛错（开机时常比程序晚就绪），Alive=false 交给看护重试；
///   · 缓冲上限 200ms，超限**丢最旧**（采集/播放两个独立时钟的速率微差会让
///     积压一路涨，延迟变成“听到几百毫秒前的声音”）；
///   · WasapiOut 共享模式 48k/16bit/立体声。
/// </summary>
internal sealed class AudioSink : IDisposable
{
    private const int SrOut = 48000;
    private static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(200);

    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    public bool Alive { get; private set; }
    public string DeviceName { get; private set; } = "?";

    /// <summary>找 CABLE 播放端点（两级匹配：先精确 "CABLE Input" 再任意 "CABLE"）。
    /// ★ 判定唯一出处 —— 本类 Start() 与 CableSetup.Installed() 共用：
    /// 「什么算 VB-CABLE」的规则只写一份，将来驱动改名/撞名设备不会两边判不一致。</summary>
    internal static MMDevice? FindCableDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return list.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>找 CABLE Input 并打开输出流。成功=true；找不到不抛（看护会再试）。</summary>
    public bool Start()
    {
        Stop();
        try
        {
            var dev = FindCableDevice();
            if (dev is null) return false;

            var wf = new WaveFormat(SrOut, 16, 2);
            _buffer = new BufferedWaveProvider(wf)
            {
                BufferDuration = TimeSpan.FromSeconds(1),   // 内部环形缓冲 1s（足够 200ms 纪律工作）
                DiscardOnBufferOverflow = false,
            };
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
            _out.Init(_buffer);
            _out.Play();
            DeviceName = dev.FriendlyName;
            Alive = true;
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    /// <summary>喂 48k/16bit/立体声字节。调用方：音频通知回调线程。</summary>
    public void Feed(byte[] pcm)
    {
        if (!Alive || _buffer is null) return;
        try
        {
            // v1 纪律：超 200ms 丢最旧（BufferedWaveProvider 只会丢新，自己动手读掉头部）
            if (_buffer.BufferedDuration > MaxLatency)
            {
                int target = (int)(SrOut * 4 * 0.18);       // 回落到 180ms
                int drop = Math.Max(0, _buffer.BufferedBytes - target);
                if (drop > 0)
                {
                    var trash = new byte[drop];
                    _buffer.Read(trash, 0, drop);
                }
            }
            _buffer.AddSamples(pcm, 0, pcm.Length);
        }
        catch
        {
            Alive = false;                                   // 设备被拔/音频服务重启
        }
    }

    /// <summary>
    /// 直通喂（MicRelay 用）：48k/16bit/立体声字节不经过管线直接进缓冲。
    /// </summary>
    public void FeedRaw(byte[] pcm) => Feed(pcm);

    public void Stop()
    {
        Alive = false;
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
