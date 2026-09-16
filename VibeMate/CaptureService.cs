using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 捕获服务（学习模式的「动作捕获」）：挂 WH_KEYBOARD_LL + WH_MOUSE_LL，
/// 抓到第一个**物理**输入（键盘键/滚轮/鼠标键）即停止并给出映射建议。
///
/// 对齐 v1「按键捕获」的语义：
///   · 滚轮 → holdmouse + wheel_up/down（按住连续滚，v1 实测更好用）
///   · 鼠标左/中/右 → mouse 单击
///   · 键 + 无修饰 → key；带修饰 → combo
/// ★ 必须过滤 LLKHF_INJECTED（0x10）：我们自己的映射输出（SendInput）也是
///   全局输入，不过滤的话学习时按遥控器会自我干扰。
/// </summary>
internal static class CaptureService
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const uint WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    private const uint WM_MOUSEWHEEL = 0x20A;
    private const int LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public nuint dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProc proc, nint hMod, uint tid);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint tid, uint msg, nuint wp, nint lp);

    // 防 GC：委托必须静态持有
    private static HookProc? _kbProc, _msProc;
    private static nint _kbHook, _msHook;
    private static volatile JsonObject? _result;
    private static volatile bool _running;
    private static uint _tid;

    public static bool Start()
    {
        if (_running) return true;
        _result = null;
        _running = true;
        var t = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        t.Start();
        return true;
    }

    public static JsonObject Poll() => _running
        ? new JsonObject { ["done"] = false }
        : _result ?? new JsonObject { ["done"] = false, ["msg"] = "没有捕获到输入（超时）" };

    private static void CaptureLoop()
    {
        _tid = GetCurrentThreadId();
        _kbProc = KbProc;
        _msProc = MsProc;
        _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, nint.Zero, 0);
        _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, nint.Zero, 0);
        if (_kbHook == nint.Zero && _msHook == nint.Zero)
        {
            _running = false;
            _result = new JsonObject { ["done"] = false, ["msg"] = "挂钩子失败" };
            return;
        }

        // 消息泵（LL 钩子回调需要）；15 秒无输入自动结束
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_running && sw.ElapsedMilliseconds < 15000)
        {
            // PeekMessage 空转会忙等 —— 用 MsgWaitOne：简化为 PostThreadMessage 唤醒 + sleep 片
            if (HaveQuitMessage()) break;
            Thread.Sleep(100);
        }
        Unhook(_kbHook); Unhook(_msHook);
        _running = false;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; }

    private static bool HaveQuitMessage()
    {
        // WM_APP+1 = 捕获完成，主循环用 PostThreadMessage 唤醒
        if (PeekMessageW(out var m, nint.Zero, 0x8001, 0x8001, 1 /*PM_REMOVE*/)) return true;
        return false;
    }
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    private static void Unhook(nint h) { if (h != nint.Zero) UnhookWindowsHookEx(h); }

    private static void Done(JsonObject r)
    {
        _result = r;
        _running = false;
        PostThreadMessageW(_tid, 0x8001, 0, 0);   // 唤醒捕获循环收尾
    }

    private static nint KbProc(int code, nuint wp, nint lp)
    {
        if (code >= 0 && (wp == WM_KEYDOWN || wp == WM_SYSKEYDOWN) && _running)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lp);
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                var vk = (int)k.vkCode;
                if (vk is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C))
                {
                    // 组合键修饰：查物理状态（名字统一到 KeysInput.VK 的形态）
                    var mods = new List<string>();
                    if (Down(0x11)) mods.Add(RightDown(0xA3) ? "RCTRL" : "CTRL");
                    if (Down(0x10)) mods.Add(RightDown(0xA1) ? "RSHIFT" : "SHIFT");
                    if (Down(0x12)) mods.Add(RightDown(0xA5) ? "RALT" : "ALT");
                    if (Down(0x5B) && !RightDown(0x5C)) mods.Add("LWIN");
                    else if (Down(0x5C)) mods.Add("RWIN");
                    var name = KeysInput.VK.FirstOrDefault(p => p.Value == (ushort)vk).Key;
                    if (name is not null)
                    {
                        var modsArr = new JsonArray();
                        foreach (var mo in mods) modsArr.Add(mo);
                        Done(new JsonObject
                        {
                            ["done"] = true,
                            ["type"] = mods.Count > 0 ? "combo" : "key",
                            ["value"] = name,
                            ["mods"] = modsArr,
                            ["label"] = string.Join('+', mods.Append(name)),
                        });
                    }
                }
            }
        }
        return CallNextHookEx(_kbHook, code, wp, lp);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static bool RightDown(int vk) => Down(vk) && !Down(vk - 1); // 粗略：右按下且左没按

    private static nint MsProc(int code, nuint wp, nint lp)
    {
        if (code >= 0 && _running)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
            if ((m.flags & LLKHF_INJECTED) == 0)
            {
                switch (wp)
                {
                    case WM_MOUSEWHEEL:
                    {
                        var dir = (short)(m.mouseData >> 16);   // 正=向上滚
                        Done(new JsonObject
                        {
                            ["done"] = true,
                            ["type"] = "holdmouse",
                            ["value"] = dir > 0 ? "wheel_up" : "wheel_down",
                            ["mods"] = new JsonArray(),
                            ["label"] = dir > 0 ? "滚轮↑（按住连续滚）" : "滚轮↓（按住连续滚）",
                        });
                        break;
                    }
                    case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                    {
                        var v = wp switch
                        { WM_LBUTTONDOWN => "left", WM_RBUTTONDOWN => "right", _ => "middle" };
                        Done(new JsonObject
                        {
                            ["done"] = true, ["type"] = "mouse", ["value"] = v,
                            ["mods"] = new JsonArray(), ["label"] = "鼠标" + v,
                        });
                        break;
                    }
                }
            }
        }
        return CallNextHookEx(_msHook, code, wp, lp);
    }
}
