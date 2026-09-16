namespace VibeMate;

/// <summary>
/// 音频管线 —— v1 voice.py 五级流水线的一比一移植（参数见 _design_notes.md §5）：
///
///   128B IMA ADPCM（高 nibble 先）→ int16@16k
///     → DcBlocker(0.995)            去直流
///     → HighPass(100Hz, Q=0.707)    切低频漂移/隆隆声（不切 AGC 会被顶死）
///     → Resampler3x                 16k→48k 线性插值，跨块保相位
///     → Agc(0.35, 1~12x, 0.995)     峰值跟踪
///     → tanh 软限幅(k=0.9)          → int16 立体声（左右同相）
///
/// 输入：128 字节 ADPCM 帧（ATVV ab5e0003 的通知负载）
/// 输出：byte[]（48k/16bit/2ch，恰好 1536 字节/帧）
/// 线程约定：只在音频回调线程喂（与 v1 相同的单线程假设），无需加锁。
/// </summary>
internal sealed class AudioPipeline
{
    private const int SrIn = 16000, SrOut = 48000;
    private const double HpFc = 100.0;         // v1 HP_FC：人声 300-3400Hz，100Hz 安全
    private const double AgcTarget = 0.35, AgcLo = 1.0, AgcHi = 12.0, AgcDecay = 0.995;
    private const double SoftK = 0.9;

    // ---- v1 audio 设置的热属性（ConfigChanged 时即时改）----
    /// <summary>AGC 开（v1 默认开）。关则用固定增益。</summary>
    public volatile bool UseAgc = true;
    /// <summary>固定增益（AGC 关闭时生效，v1 默认 4.0）。</summary>
    public volatile float FixedGain = 4.0f;

    // ---------------- IMA ADPCM（v1 voice.py:41 STEP/INDEX 表）----------------
    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24686, 27086, 29794,
        32767,
    };
    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    private int _pred, _idx;

    // ---------------- 滤波器状态 ----------------
    private double _dcX1, _dcY1;                                    // DcBlocker
    private double _hpX1, _hpX2, _hpY1, _hpY2;                      // biquad
    private readonly double _b0, _b1, _b2, _a1, _a2;                // biquad 系数
    private double _resPrev;                                        // 重采样相位保持
    private double _agcPeak;                                        // AGC
    private double _agcGain = 1.0;
    private int _agcBlocks;

    public AudioPipeline()
    {
        // biquad 系数由固定 fc/sr 决定，构造一次即可
        (_b0, _b1, _b2, _a1, _a2) = DesignHighPass(HpFc, SrIn, 0.707);
        Reset();
    }

    public void Reset()
    {
        _pred = 0; _idx = 0;
        _dcX1 = _dcY1 = 0;
        _hpX1 = _hpX2 = _hpY1 = _hpY2 = 0;
        _resPrev = 0;
        _agcPeak = 0; _agcGain = 1.0; _agcBlocks = 0;
    }

    /// <summary>RBJ biquad 高通设计（v1 HighPass.__init__ 的公式）。</summary>
    private static (double, double, double, double, double) DesignHighPass(double fc, double sr, double q)
    {
        double w0 = 2.0 * Math.PI * fc / sr;
        double cw = Math.Cos(w0), sw = Math.Sin(w0);
        double alpha = sw / (2.0 * q);
        double b0 = (1 + cw) / 2, b1 = -(1 + cw), b2 = (1 + cw) / 2;
        double a0 = 1 + alpha, a1 = -2 * cw, a2 = 1 - alpha;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>喂一帧（128B ADPCM），返回 48k/16bit/立体声字节。</summary>
    public byte[] Feed(byte[] frame)
    {
        // ---- 1) ADPCM 解码（★ 高 nibble 先 —— ATVV 与 IMA 标准相反，
        //        写反直流偏 43.8% 满量程，v1 的决定性实测证据）
        int n = frame.Length * 2;
        var pcm = new double[n];
        int p = _pred, x = _idx, o = 0;
        foreach (var by in frame)
        {
            foreach (var nib in new[] { by >> 4, by & 0x0F })
            {
                int step = StepTable[x];
                int diff = step >> 3;
                if ((nib & 1) != 0) diff += step >> 2;
                if ((nib & 2) != 0) diff += step >> 1;
                if ((nib & 4) != 0) diff += step;
                p = (nib & 8) != 0 ? p - diff : p + diff;
                if (p > 32767) p = 32767; else if (p < -32768) p = -32768;
                x += IndexTable[nib];
                if (x < 0) x = 0; else if (x > 88) x = 88;
                pcm[o++] = p;
            }
        }
        _pred = p; _idx = x;

        // ---- 2) 去直流（y[n]=x[n]-x[n-1]+a*y[n-1]）
        for (int i = 0; i < n; i++)
        {
            double y = pcm[i] - _dcX1 + 0.995 * _dcY1;
            _dcX1 = pcm[i]; _dcY1 = y;
            pcm[i] = y;
        }

        // ---- 3) 高通 biquad
        for (int i = 0; i < n; i++)
        {
            double v = pcm[i];
            double y = _b0 * v + _b1 * _hpX1 + _b2 * _hpX2 - _a1 * _hpY1 - _a2 * _hpY2;
            _hpX2 = _hpX1; _hpX1 = v; _hpY2 = _hpY1; _hpY1 = y;
            pcm[i] = y;
        }

        // ---- 4) 3x 线性插值升采样（跨块保持相位：从上一块末样本出发）
        var up = new double[n * 3];
        {
            double prev = _resPrev;
            int u = 0;
            foreach (var v in pcm)
            {
                double stepv = (v - prev) / 3.0;
                double cur = prev;
                for (int k = 0; k < 3; k++) { cur += stepv; up[u++] = cur; }
                prev = v;
            }
            _resPrev = prev;
        }

        // ---- 5) AGC（峰值跟踪 + 每 20 块调一次增益）；关闭则固定增益（v1 语义）
        double g;
        if (UseAgc)
        {
            double peak = 0;
            foreach (var v in up)
            {
                double a = Math.Abs(v / 32768.0);
                if (a > peak) peak = a;
            }
            _agcPeak = Math.Max(peak, _agcPeak * AgcDecay);
            if (_agcBlocks % 20 == 0 && _agcPeak > 1e-4)
                _agcGain = Math.Clamp(AgcTarget / _agcPeak, AgcLo, AgcHi);
            _agcBlocks++;
            g = _agcGain;
        }
        else g = FixedGain;

        // ---- 6) 软限幅 + int16 立体声
        var outBytes = new byte[up.Length * 4];
        int ob = 0;
        foreach (var v in up)
        {
            double s = g * (v / 32768.0);
            int samp = (int)(SoftK * Math.Tanh(s / SoftK) * 32767.0);
            if (samp > 32767) samp = 32767; else if (samp < -32768) samp = -32768;
            ushort u16 = (ushort)samp;
            outBytes[ob++] = (byte)(u16 & 0xFF);
            outBytes[ob++] = (byte)(u16 >> 8);
            outBytes[ob++] = (byte)(u16 & 0xFF);      // 右通道同相复制
            outBytes[ob++] = (byte)(u16 >> 8);
        }
        return outBytes;
    }
}
