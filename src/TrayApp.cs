using System.Diagnostics;

namespace IpadBridgeBle;

public class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly Config _config;
    readonly BleHidPeripheral _hid;
    readonly InputCapture _capture;
    readonly OverlayForm _overlay;
    readonly RawTouchpad _touchpad;
    readonly System.Windows.Forms.Timer _flushTimer;
    readonly Control _sync;
    readonly ToolStripMenuItem _statusItem;

    readonly Icon _iconDisconnected = MakeIcon(Color.Gray);
    readonly Icon _iconConnected = MakeIcon(Color.DeepSkyBlue);
    readonly Icon _iconActive = MakeIcon(Color.LimeGreen);

    public TrayApp()
    {
        _sync = new Control();
        _sync.CreateControl();

        _config = Config.Load();
        _hid = new BleHidPeripheral();
        _capture = new InputCapture(_config, _hid);
        _overlay = new OverlayForm();
        _overlay.WheelDelta += (delta, horizontal) => _capture.HandleWheel(delta, horizontal);
        _overlay.ExitRequested += () => _capture.ExitIpadMode();

        _touchpad = new RawTouchpad();
        _touchpad.Swipe += OnSwipe;

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("iPad: waiting to pair") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Toggle iPad mode\tCtrl+Alt+I", null, (_, _) => _capture.ToggleIpadMode());
        menu.Items.Add("Edit config", null, (_, _) => Process.Start("notepad.exe", Config.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = _iconDisconnected,
            Text = "iPad Bridge (BLE) — pair from the iPad",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _capture.ToggleIpadMode();

        _hid.ConnectionChanged += connected => _sync.BeginInvoke(() =>
        {
            if (!connected) _capture.ExitIpadMode();
            UpdateStatus(connected);
        });
        _capture.ModeChanged += active => _sync.BeginInvoke(() =>
        {
            if (active) _overlay.ShowOverlay();
            else _overlay.HideOverlay();
            UpdateStatus(_hid.Connected);
        });

        _flushTimer = new System.Windows.Forms.Timer { Interval = 8 };
        _flushTimer.Tick += (_, _) => { if (_capture.IpadMode) _capture.FlushPendingMouse(); };
        _flushTimer.Start();

        GestureGuard.Restore(); // undo any leftover per-mode disable from older builds

        RawTouchpad.Debug = File.Exists(Path.Combine(AppContext.BaseDirectory, "debug_touchpad"));
        _capture.Install();
        _touchpad.Install();
        StartPeripheral();
    }

    // HID usages: CapsLock(=Globe)=0x39, H=0x0B, arrows Right/Left/Down/Up=0x4F/0x50/0x51/0x52
    void OnSwipe(RawTouchpad.SwipeDir dir)
    {
        if (!_capture.IpadMode || !_hid.Connected) return;
        switch (dir)
        {
            case RawTouchpad.SwipeDir.Up: _hid.SendShortcut(0xE3, 0x0B); break;     // Cmd+H     Home (quick up)
            case RawTouchpad.SwipeDir.UpHold: _hid.SendShortcut(0x39, 0x52); break; // Globe+Up   App Switcher (up & hold)
            case RawTouchpad.SwipeDir.Left: _hid.SendShortcut(0x39, 0x4F); break;   // Globe+Right next app (reversed)
            case RawTouchpad.SwipeDir.Right: _hid.SendShortcut(0x39, 0x50); break;  // Globe+Left  prev app (reversed)
            // Down: unmapped (up / up-hold cover Home + recents)
        }
    }

    async void StartPeripheral()
    {
        try
        {
            await _hid.StartAsync();
            _sync.BeginInvoke(() => _icon.ShowBalloonTip(8000, "iPad Bridge (BLE)",
                "On the iPad: Settings → Bluetooth, and pair with this PC.", ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            _sync.BeginInvoke(() => _icon.ShowBalloonTip(10000, "iPad Bridge (BLE)",
                "Could not start Bluetooth HID: " + ex.Message, ToolTipIcon.Error));
        }
    }

    void UpdateStatus(bool connected)
    {
        string state = _capture.IpadMode ? "iPAD MODE (Ctrl+Alt+I to exit)"
                     : connected ? "paired — push right edge or Ctrl+Alt+I"
                     : "waiting to pair (iPad → Bluetooth)";
        _icon.Icon = _capture.IpadMode ? _iconActive : connected ? _iconConnected : _iconDisconnected;
        _icon.Text = Truncate("iPad Bridge (BLE) — " + state, 63);
        _statusItem.Text = "iPad: " + (connected ? "paired" : "waiting to pair");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 12, 12);
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _flushTimer.Stop();
        _capture.Dispose();
        _touchpad.Dispose();
        _overlay.HideOverlay();
        _overlay.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
