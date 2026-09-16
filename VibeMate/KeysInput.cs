using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 键盘合成（SendInput）—— v1 keys.py 的一比一移植（语音键部分）。
///
/// 两条 v1 实测教训必须保留：
///   · EXT_VK 集合要带 KEYEVENTF_EXTENDEDKEY：少了它 Windows 把「右 Alt」当
///     左 Alt、方向键当小键盘 —— 输入法就收不到语音快捷键；
///   · 扫描码一并给出（MapVirtualKey 动态取，等价 v1 的手写 SCAN 表）：
///     有些输入法/游戏只认扫描码。
/// </summary>
internal static class KeysInput
{
    // ---------------- P/Invoke ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    /// <summary>union 必须 32 字节（MOUSEINPUT 最大）——用 Explicit 让 ki/mi 共享
    /// 偏移 8，天然正确（v1 keys.py:127 的坑：union 尺寸不对 SendInput 静默 err=87）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;              // 1=键盘 0=鼠标
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint vk, uint mapType);   // 0 = VK_TO_VSC

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001,
                       KEYEVENTF_UNICODE = 0x0004;
    private const uint INPUT_KEYBOARD = 1, INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800,
                       MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                       MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
                       MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
                       MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int WheelDelta = 120;

    // ---------------- VK 表（照抄 v1 keys.py:37-71）----------------
    internal static readonly Dictionary<string, ushort> VK = new(StringComparer.OrdinalIgnoreCase);

    static KeysInput()
    {
        var pairs = new Dictionary<ushort, string[]>
        {
            [0x11] = new[] { "CTRL" }, [0x10] = new[] { "SHIFT" }, [0x12] = new[] { "ALT" },
            [0xA2] = new[] { "LCTRL" }, [0xA3] = new[] { "RCTRL" },
            [0xA0] = new[] { "LSHIFT" }, [0xA1] = new[] { "RSHIFT" },
            [0xA4] = new[] { "LALT" }, [0xA5] = new[] { "RALT" },
            [0x5B] = new[] { "LWIN" }, [0x5C] = new[] { "RWIN" }, [0x5D] = new[] { "APPS" },
            [0x08] = new[] { "BACKSPACE" }, [0x09] = new[] { "TAB" },
            [0x0D] = new[] { "ENTER" }, [0x1B] = new[] { "ESC" }, [0x20] = new[] { "SPACE" },
            [0x21] = new[] { "PAGEUP" }, [0x22] = new[] { "PAGEDOWN" },
            [0x23] = new[] { "END" }, [0x24] = new[] { "HOME" },
            [0x25] = new[] { "LEFT" }, [0x26] = new[] { "UP" },
            [0x27] = new[] { "RIGHT" }, [0x28] = new[] { "DOWN" },
            [0x2D] = new[] { "INSERT" }, [0x2E] = new[] { "DELETE" },
            [0x14] = new[] { "CAPSLOCK" }, [0x2C] = new[] { "PRINTSCREEN" },
            [0x90] = new[] { "NUMLOCK" }, [0x91] = new[] { "SCROLLLOCK" }, [0x13] = new[] { "PAUSE" },
            [0xBA] = new[] { "SEMICOLON" }, [0xBB] = new[] { "EQUAL" }, [0xBC] = new[] { "COMMA" },
            [0xBD] = new[] { "MINUS" }, [0xBE] = new[] { "PERIOD" }, [0xBF] = new[] { "SLASH" },
            [0xC0] = new[] { "BACKQUOTE" }, [0xDB] = new[] { "LBRACKET" },
            [0xDC] = new[] { "BACKSLASH" }, [0xDD] = new[] { "RBRACKET" }, [0xDE] = new[] { "QUOTE" },
            [0xA6] = new[] { "BROWSER_BACK" }, [0xA7] = new[] { "BROWSER_FORWARD" },
            [0xA8] = new[] { "BROWSER_REFRESH" }, [0xA9] = new[] { "BROWSER_STOP" },
            [0xAA] = new[] { "BROWSER_SEARCH" }, [0xAB] = new[] { "BROWSER_FAVORITES" },
            [0xAC] = new[] { "BROWSER_HOME" },
        };
        foreach (var (vk, names) in pairs)
            foreach (var n in names) VK[n] = vk;
        for (char c = 'A'; c <= 'Z'; c++) VK[c.ToString()] = (ushort)c;
        for (char d = '0'; d <= '9'; d++) VK["DIGIT" + d] = (ushort)d;
        for (int i = 1; i <= 24; i++) VK["F" + i] = (ushort)(0x6F + i);
    }

    /// <summary>必须带 EXTENDEDKEY 的键（v1 keys.py:75 —— 右 Alt/右 Ctrl/方向键等）。</summary>
    private static readonly HashSet<ushort> ExtVk = new()
    {
        0xA3, 0xA5, 0x5B, 0x5C, 0x5D, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,
        0x26, 0x28, 0x25, 0x27, 0x5C,   // 含 NumLock/Pause 等
    };

    public static ushort? VkOf(string? name)
        => name is not null && VK.TryGetValue(name.Trim(), out var vk) ? vk : null;

    // ---------------- 合成 ----------------
    private static bool Send(IEnumerable<(ushort vk, bool up)> keys)
    {
        var list = keys.Select(k =>
        {
            var inp = new INPUT { type = INPUT_KEYBOARD };
            inp.ki.wVk = k.vk;
            inp.ki.wScan = MapVirtualKey(k.vk, 0);
            uint flags = 0;
            if (ExtVk.Contains(k.vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            if (k.up) flags |= KEYEVENTF_KEYUP;
            inp.ki.dwFlags = flags;
            return inp;
        }).ToArray();
        if (list.Length == 0) return true;
        var sent = SendInput((uint)list.Length, list, Marshal.SizeOf<INPUT>());
        if (sent != list.Length)
            OnError?.Invoke($"SendInput 只发出 {sent}/{list.Length}（err={Marshal.GetLastWin32Error()}；sizeof(INPUT)={Marshal.SizeOf<INPUT>()}，应为 40）");
        return sent == list.Length;
    }

    /// <summary>合成失败时的诊断回调（Program 里接到日志）。</summary>
    internal static event Action<string>? OnError;

    public static bool KeyDown(ushort vk) => Send(new[] { (vk, false) });
    public static bool KeyUp(ushort vk) => Send(new[] { (vk, true) });

    /// <summary>组合键一次成型：修饰键按下→主键点按→修饰键反序抬起（v1 tap 语义）。</summary>
    public static bool Combo(IEnumerable<ushort> mods, ushort key)
    {
        var seq = new List<(ushort, bool)>();
        foreach (var m in mods) seq.Add((m, false));
        seq.Add((key, false));
        seq.Add((key, true));
        foreach (var m in mods.Reverse()) seq.Add((m, true));
        return Send(seq);
    }

    /// <summary>hold 语义的按下：修饰键→主键，保持到 KeyUp 反序抬起。</summary>
    public static void KeyDown2(ushort[] mods, ushort key)
    {
        foreach (var m in mods) KeyDown(m);
        KeyDown(key);
    }

    /// <summary>鼠标动作（v1 keys.py 的 wheel/left/right/middle）。</summary>
    public static bool Mouse(string action)
    {
        var (flags, data) = action switch
        {
            "wheel_up" => (MOUSEEVENTF_WHEEL, unchecked((uint)(-WheelDelta))),
            "wheel_down" => (MOUSEEVENTF_WHEEL, (uint)WheelDelta),
            "left" => (MOUSEEVENTF_LEFTDOWN, 0u),
            "left_up" => (MOUSEEVENTF_LEFTUP, 0u),
            "right" => (MOUSEEVENTF_RIGHTDOWN, 0u),
            "right_up" => (MOUSEEVENTF_RIGHTUP, 0u),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, 0u),
            "middle_up" => (MOUSEEVENTF_MIDDLEUP, 0u),
            _ => (0u, 0u),
        };
        if (flags == 0) return false;
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.mi.dwFlags = flags;
        inp.mi.mouseData = data;
        var ok = SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>()) == 1;
        if (!ok) OnError?.Invoke($"鼠标 SendInput 失败 err={Marshal.GetLastWin32Error()}");
        return ok;
    }

    /// <summary>文本上屏：KEYEVENTF_UNICODE 逐字符（v1 text 类型）。</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD };
            down.ki.wScan = ch;
            down.ki.dwFlags = KEYEVENTF_UNICODE;
            var up = new INPUT { type = INPUT_KEYBOARD };
            up.ki.wScan = ch;
            up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5);                     // 输入法跟随节奏，太快会吞字
        }
    }

    public static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}

/// <summary>
/// 语音键 → 键盘动作（v1 VoiceKey 一比一）：
/// hold = 修饰键按下→主键按下，松开反序抬起（输入法「按住说话」）；
/// tap = 完整一次点击（Win+H 开关式）。
/// </summary>
internal sealed class VoiceKey
{
    public readonly bool Ok;
    private readonly string _mode;
    private readonly ushort[] _mods;
    private readonly ushort _key;
    private bool _down;

    public string Label { get; }

    /// <summary>主键 VK（MicRelay 判「按住键盘语音快捷键」用）；无效配置为 null。</summary>
    internal ushort? KeyVk => Ok ? (_key != 0 ? _key : (_mods.Length > 0 ? _mods[0] : null)) : null;

    public VoiceKey(JsonNode? spec)
    {
        spec ??= new JsonObject { ["mode"] = "hold", ["key"] = "RALT", ["mods"] = new JsonArray() };
        _mode = spec["mode"]?.GetValue<string>() is "tap" or "hold" ? spec["mode"]!.GetValue<string>() : "hold";
        // 主键可为空（「（无）」选项）：纯修饰键组合（如微信的按住 Ctrl+LWin）
        var keyName = spec["key"]?.GetValue<string>() ?? "";
        var key = string.IsNullOrWhiteSpace(keyName) ? (ushort?)null
                 : KeysInput.VkOf(keyName);
        _mods = (spec["mods"] as JsonArray ?? new JsonArray())
            .Select(m => KeysInput.VkOf(m?.GetValue<string>())).Where(v => v.HasValue)
            .Select(v => v!.Value).ToArray();
        if (key is null && _mods.Length == 0)
        { Ok = false; _key = 0; Label = "?"; return; }          // 键和修饰全空才无效
        _key = key ?? 0;                                          // 0 = 无主键
        Ok = true;
        var names = _mods.Select(v => KeysInput.VK.First(p => p.Value == v).Key).ToList();
        if (_key != 0) names.Add(KeysInput.VK.First(p => p.Value == _key).Key);
        Label = string.Join("+", names);
    }

    public void Press()
    {
        if (!Ok || _down) return;
        _down = true;
        foreach (var m in _mods) KeysInput.KeyDown(m);
        if (_key != 0) KeysInput.KeyDown(_key);
    }

    public void Release()
    {
        if (!Ok || !_down) return;
        _down = false;
        if (_key != 0) KeysInput.KeyUp(_key);
        for (int i = _mods.Length - 1; i >= 0; i--) KeysInput.KeyUp(_mods[i]);
    }

    public void Tap()
    {
        if (!Ok) return;
        foreach (var m in _mods) KeysInput.KeyDown(m);
        if (_key != 0) { KeysInput.KeyDown(_key); KeysInput.KeyUp(_key); }
        for (int i = _mods.Length - 1; i >= 0; i--) KeysInput.KeyUp(_mods[i]);
    }

    /// <summary>启动时补一次抬起：上次进程在「按住」期间被杀，键会一直卡在按下态。</summary>
    public bool ClearStuck()
    {
        if (!Ok) return false;
        var all = _mods.Append(_key).ToArray();
        if (!all.Any(KeysInput.IsDown)) return false;
        KeysInput.KeyUp(_key);
        for (int i = _mods.Length - 1; i >= 0; i--) KeysInput.KeyUp(_mods[i]);
        return true;
    }

    public void Fire(bool pressed)
    {
        if (_mode == "tap") { if (pressed) Tap(); }
        else if (pressed) Press();
        else Release();
    }
}
