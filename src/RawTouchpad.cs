using System.Runtime.InteropServices;

namespace IpadBridgeBle;

/// <summary>
/// Reads raw precision-touchpad HID reports (per-finger contacts) that the
/// low-level mouse hook cannot see, and detects 3-finger swipes. Windows also
/// processes these contacts for its own gestures, so those must be turned off
/// (see the one-time setup) to avoid double actions.
///
/// Emits Swipe(direction) once per gesture, on finger lift.
/// </summary>
public class RawTouchpad : IDisposable
{
    public enum SwipeDir { Up, UpHold, Down, Left, Right }
    public event Action<SwipeDir> Swipe;

    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const int RIM_TYPEHID = 2;

    // HID usage pages / usages
    const ushort UP_GENERIC = 0x01, U_X = 0x30, U_Y = 0x31;
    const ushort UP_DIGITIZER = 0x0D, U_CONTACT_COUNT = 0x54, U_TIP = 0x42, U_CONTACT_ID = 0x51;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint num, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLen);

    readonly Dictionary<IntPtr, byte[]> _preparsedCache = new();
    MessageWindow _window;

    // Gesture tracking (touchpad logical units)
    bool _active;
    int _startX, _startY, _lastX, _lastY, _maxFingers, _rangeX = 3000, _rangeY = 2000;
    long _lastMoveMs, _lastReportMs, _dwellMs;
    readonly object _gLock = new();
    System.Threading.Timer _silenceTimer;
    const int SilenceMs = 90;  // fingers lifted → pad quiet this long → end gesture
    const int HoldMs = 280;    // fingers stationary this long at the end = "hold"
    const int MoveEps = 40;    // logical units of motion that counts as "still moving"

    public void Install()
    {
        _window = new MessageWindow(OnInput);
        var rid = new RAWINPUTDEVICE
        {
            UsagePage = UP_DIGITIZER,
            Usage = 0x05, // Touch Pad
            Flags = RIDEV_INPUTSINK,
            hwndTarget = _window.Handle
        };
        _silenceTimer = new System.Threading.Timer(OnSilence, null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        bool ok = RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log($"Install: RegisterRawInputDevices ok={ok} err={Marshal.GetLastWin32Error()} hwnd={_window.Handle}");
    }

    int _inputCount;

    byte[] GetPreparsed(IntPtr hDevice)
    {
        if (_preparsedCache.TryGetValue(hDevice, out var cached)) return cached;
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) { _preparsedCache[hDevice] = null; return null; }
        var buf = new byte[size];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, h.AddrOfPinnedObject(), ref size); }
        finally { h.Free(); }
        _preparsedCache[hDevice] = buf;
        return buf;
    }

    void OnInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
                return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return;

            byte[] preparsed = GetPreparsed(header.hDevice);
            if (preparsed == null) return;

            int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
            uint dwSizeHid = (uint)Marshal.ReadInt32(buffer, headerSize);
            uint dwCount = (uint)Marshal.ReadInt32(buffer, headerSize + 4);
            int reportOffset = headerSize + 8;

            var pinned = GCHandle.Alloc(preparsed, GCHandleType.Pinned);
            try
            {
                IntPtr pp = pinned.AddrOfPinnedObject();
                if (HidP_GetCaps(pp, out var caps) != 0x00110000 /*HIDP_STATUS_SUCCESS is 0x110000*/ && caps.InputReportByteLength == 0)
                {
                    // Some drivers return success as 0x00110000; fall through anyway.
                }
                for (int i = 0; i < dwCount; i++)
                {
                    var report = new byte[dwSizeHid];
                    Marshal.Copy(buffer + reportOffset + i * (int)dwSizeHid, report, 0, (int)dwSizeHid);
                    ProcessReport(pp, report, dwSizeHid);
                }
            }
            finally { pinned.Free(); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    void ProcessReport(IntPtr preparsed, byte[] report, uint len)
    {
        // Contact count for this frame (0 when all fingers lifted).
        int ccStatus = HidP_GetUsageValue(0, UP_DIGITIZER, 0, U_CONTACT_COUNT, out uint countVal, preparsed, report, len);
        if (ccStatus != 0x110000) countVal = 0;
        int fingers = (int)countVal;
        if (_inputCount++ < 8) Log($"input#{_inputCount}: len={len} ccStatus=0x{ccStatus:X} fingers={fingers}");

        int okX = HidP_GetUsageValue(0, UP_GENERIC, 0, U_X, out uint xVal, preparsed, report, len);
        int okY = HidP_GetUsageValue(0, UP_GENERIC, 0, U_Y, out uint yVal, preparsed, report, len);
        bool gotXy = okX == 0x110000 && okY == 0x110000;

        long now = Environment.TickCount64;
        lock (_gLock)
        {
            if (fingers >= 2)
            {
                if (!_active)
                {
                    _active = true; _maxFingers = 0;
                    if (gotXy) { _startX = (int)xVal; _startY = (int)yVal; _lastX = _startX; _lastY = _startY; }
                    _lastMoveMs = now;
                    Log($"gesture start f={fingers} x={_startX} y={_startY}");
                }
                if (fingers > _maxFingers) _maxFingers = fingers;
                if (gotXy)
                {
                    if (Math.Abs((int)xVal - _lastX) > MoveEps || Math.Abs((int)yVal - _lastY) > MoveEps)
                        _lastMoveMs = now; // still moving
                    _lastX = (int)xVal; _lastY = (int)yVal;
                }
                _lastReportMs = now;
            }
            else if (fingers == 1 && _active && gotXy)
            {
                _lastX = (int)xVal; _lastY = (int)yVal;
                _lastReportMs = now;
            }
            if (_active)
                _silenceTimer?.Change(SilenceMs, System.Threading.Timeout.Infinite);
        }
    }

    void OnSilence(object _)
    {
        int dx, dy, fingers; long dwell;
        lock (_gLock)
        {
            if (!_active) return;
            _active = false;
            dx = _lastX - _startX; dy = _lastY - _startY; fingers = _maxFingers;
            dwell = _lastReportMs - _lastMoveMs; // time fingers stayed still before lifting
        }
        Finalize(dx, dy, fingers, dwell);
    }

    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "touchpad.log");
    public static bool Debug = true;
    static void Log(string m) { if (Debug) try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {m}\r\n"); } catch { } }

    void Finalize(int dx, int dy, int fingers, long dwell)
    {
        Log($"gesture end: fingers={fingers} dx={dx} dy={dy} dwell={dwell}");
        if (fingers != 3) return; // 3-finger swipes only (for now)
        const int thX = 300, thY = 260;   // per-axis minimum travel
        const double VBias = 1.6;          // treat as vertical only if clearly vertical (staggered fingers lean the axis)

        int adx = Math.Abs(dx), ady = Math.Abs(dy);
        SwipeDir dir;
        if (ady > adx * VBias) // vertical
        {
            if (ady < thY) { Log("  -> below threshold (v)"); return; }
            dir = dy > 0 ? SwipeDir.Down : SwipeDir.Up; // touchpad Y grows downward
            if (dir == SwipeDir.Up && dwell >= HoldMs) dir = SwipeDir.UpHold;
        }
        else // horizontal (default — biased so angled swipes still read as left/right)
        {
            if (adx < thX) { Log("  -> below threshold (h)"); return; }
            dir = dx > 0 ? SwipeDir.Right : SwipeDir.Left;
        }
        Log($"  -> SWIPE {dir} (dwell={dwell})");
        Swipe?.Invoke(dir);
    }

    public void Dispose() => _window?.Destroy();

    /// <summary>Minimal hidden native window to receive WM_INPUT.</summary>
    class MessageWindow : NativeWindow
    {
        readonly Action<IntPtr> _onInput;
        public MessageWindow(Action<IntPtr> onInput)
        {
            _onInput = onInput;
            CreateHandle(new CreateParams { Caption = "IpadBridgeRawInput" });
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF) _onInput(m.LParam);
            base.WndProc(ref m);
        }
        public void Destroy() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }
}
