using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IpadBridgeBle;

/// <summary>
/// Global low-level keyboard/mouse hooks. Outside iPad mode, input flows to
/// Windows as usual (except the hotkeys). In iPad mode, all input is swallowed
/// locally and streamed to the phone. The cursor is parked in the center of the
/// primary screen so relative deltas are never clipped by screen bounds.
///
/// Hotkeys:
///   Ctrl+Alt+I      toggle iPad mode (also entered by pushing the configured screen edge)
///   Ctrl+Alt+Space  play/pause on iPad     Ctrl+Alt+M     mute
///   Ctrl+Alt+Right  next track             Ctrl+Alt+Left  previous track
///   Ctrl+Alt+Up     volume up              Ctrl+Alt+Down  volume down
/// </summary>
public class InputCapture : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WH_MOUSE_LL = 14;

    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;

    const uint LLKHF_INJECTED = 0x10;
    const uint LLMHF_INJECTED = 0x01;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    readonly Config _config;
    readonly BleHidPeripheral _client;

    // Delegates must stay referenced or the GC collects them under the hook.
    readonly HookProc _keyboardProc;
    readonly HookProc _mouseProc;
    IntPtr _keyboardHook;
    IntPtr _mouseHook;

    public bool IpadMode { get; private set; }
    public event Action<bool> ModeChanged;

    POINT _anchor;
    double _pendingDx, _pendingDy;
    // Estimated iPad cursor X, in sent-delta units, measured from the iPad's
    // left edge (pinned to 0 on entry). Lets us detect "pushed back off the
    // left edge" to return to the laptop, since we only send relative deltas.
    double _estIpadX;
    bool _returnArmed;
    const int ArmMargin = 220;       // move this far into the iPad before edge-return arms
    const int ReturnOvershoot = 320; // push this far past the left edge to return
    int _wheelAccV, _wheelAccH;
    const int WheelUnitsPerStep = 40; // ~3 iPad scroll steps per full 120 notch
    readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
    readonly Stopwatch _edgeCooldown = Stopwatch.StartNew();

    bool _ctrlHeld, _altHeld;
    int _buttonMask;
    readonly HashSet<int> _swallowNextUp = new();

    public InputCapture(Config config, BleHidPeripheral client)
    {
        _config = config;
        _client = client;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        timeBeginPeriod(1); // 1ms system timer so our flush cadence isn't 15.6ms-grained
        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install input hooks");
    }

    public void ToggleIpadMode()
    {
        if (IpadMode) ExitIpadMode();
        else EnterIpadMode();
    }

    public void EnterIpadMode()
    {
        if (IpadMode || !_client.Connected) return;
        var screen = Screen.PrimaryScreen.Bounds;
        _anchor = new POINT { x = screen.Left + screen.Width / 2, y = screen.Top + screen.Height / 2 };
        SetCursorPos(_anchor.x, _anchor.y);
        _pendingDx = _pendingDy = 0;
        _wheelAccV = _wheelAccH = 0;
        _buttonMask = 0;
        IpadMode = true;
        // Pin the iPad cursor to its left edge to establish a known reference,
        // then require moving into the iPad before edge-return can trigger.
        _client.SendMouseMove(-4000, 0);
        _estIpadX = 0;
        _returnArmed = false;
        ModeChanged?.Invoke(true);
    }

    public void ExitIpadMode()
    {
        if (!IpadMode) return;
        IpadMode = false;
        _client.SendReleaseAll();
        _edgeCooldown.Restart();
        ModeChanged?.Invoke(false);
    }

    /// <summary>Called by a UI timer so small trailing mouse deltas don't get stuck.</summary>
    public void FlushPendingMouse()
    {
        int dx = (int)_pendingDx;
        int dy = (int)_pendingDy;
        if (dx == 0 && dy == 0) return;
        _pendingDx -= dx; // keep sub-pixel remainder for slow, precise movement
        _pendingDy -= dy;
        _client.SendMouseMove(dx, dy);
        _sinceFlush.Restart();

        // Track estimated iPad-cursor X for edge-return to the laptop.
        _estIpadX += dx;
        if (_estIpadX > ArmMargin) _returnArmed = true;
        if (_returnArmed && _estIpadX < -ReturnOvershoot) ExitIpadMode();
    }

    /// <summary>
    /// Wheel input from either the LL hook (real mice) or the overlay window
    /// (precision-touchpad smooth scrolling, which bypasses LL hooks).
    /// Accumulates so tiny touchpad deltas aren't truncated away.
    /// </summary>
    public void HandleWheel(int rawDelta, bool horizontal)
    {
        if (!IpadMode) return;
        if (horizontal)
        {
            _wheelAccH += rawDelta; // accumulated but not sent: no AC Pan in the HID descriptor yet
            return;
        }
        _wheelAccV += _config.InvertScroll ? -rawDelta : rawDelta;
        int steps = _wheelAccV / WheelUnitsPerStep;
        if (steps == 0) return;
        _wheelAccV -= steps * WheelUnitsPerStep;
        FlushPendingMouse();
        _client.SendScroll(steps, 0);
    }

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        int vk = (int)data.vkCode;

        // Track physical modifier state (LL hooks always deliver side-specific VKs)
        if (vk == 0xA2 || vk == 0xA3) _ctrlHeld = down;
        if (vk == 0xA4 || vk == 0xA5) _altHeld = down;

        // Second half of an already-handled hotkey
        if (!down && _swallowNextUp.Remove(vk)) return 1;

        if (down && _ctrlHeld && _altHeld)
        {
            if (vk == 0x49) // I
            {
                _swallowNextUp.Add(vk);
                ToggleIpadMode();
                return 1;
            }
            if (_config.MediaHotkeys)
            {
                ushort usage = vk switch
                {
                    0x20 => HidMap.ConsumerPlayPause,  // Space
                    0x27 => HidMap.ConsumerScanNext,   // Right
                    0x25 => HidMap.ConsumerScanPrev,   // Left
                    0x26 => HidMap.ConsumerVolumeUp,   // Up
                    0x28 => HidMap.ConsumerVolumeDown, // Down
                    0x4D => HidMap.ConsumerMute,       // M
                    _ => 0
                };
                if (usage != 0)
                {
                    _swallowNextUp.Add(vk);
                    _client.SendConsumer(usage, true);
                    _client.SendConsumer(usage, false);
                    return 1;
                }
            }
        }

        if (!IpadMode) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        byte hidUsage = HidMap.ToUsage(vk, _config.MapCtrlToCmd);
        if (hidUsage != 0) _client.SendKey(hidUsage, down);
        return 1; // swallow everything while in iPad mode
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLMHF_INJECTED) != 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = (int)wParam;

        if (!IpadMode)
        {
            if (msg == WM_MOUSEMOVE && _edgeCooldown.ElapsedMilliseconds > 500 && AtEnterEdge(data.pt))
            {
                EnterIpadMode();
                // Swallow the triggering move, or it re-applies after our warp
                // to screen center and every later delta is off by (edge - center).
                return 1;
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        switch (msg)
        {
            case WM_MOUSEMOVE:
                // pt is the proposed position; the real cursor never leaves the
                // anchor because we swallow the event, so this is a clean delta.
                _pendingDx += (data.pt.x - _anchor.x) * _config.MouseSensitivity;
                _pendingDy += (data.pt.y - _anchor.y) * _config.MouseSensitivity;
                if (_sinceFlush.ElapsedMilliseconds >= 7) FlushPendingMouse();
                break;

            case WM_LBUTTONDOWN: SetButton(0, true); break;
            case WM_LBUTTONUP: SetButton(0, false); break;
            case WM_RBUTTONDOWN: SetButton(1, true); break;
            case WM_RBUTTONUP: SetButton(1, false); break;
            case WM_MBUTTONDOWN: SetButton(2, true); break;
            case WM_MBUTTONUP: SetButton(2, false); break;

            case WM_MOUSEWHEEL:
                HandleWheel((short)(data.mouseData >> 16), false);
                break;
            case WM_MOUSEHWHEEL:
                HandleWheel((short)(data.mouseData >> 16), true);
                break;
        }
        return 1; // swallow all mouse input while in iPad mode
    }

    void SetButton(int bit, bool down)
    {
        FlushPendingMouse();
        _buttonMask = down ? _buttonMask | (1 << bit) : _buttonMask & ~(1 << bit);
        _client.SendMouseButtons(_buttonMask);
    }

    bool AtEnterEdge(POINT pt)
    {
        var vs = SystemInformation.VirtualScreen;
        return _config.EnterEdge switch
        {
            "right" => pt.x >= vs.Right - 1,
            "left" => pt.x <= vs.Left,
            "top" => pt.y <= vs.Top,
            "bottom" => pt.y >= vs.Bottom - 1,
            _ => false
        };
    }

    public void Dispose()
    {
        ExitIpadMode();
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
        timeEndPeriod(1);
    }
}
