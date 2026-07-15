using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace IpadBridgeBle;

/// <summary>
/// Turns this PC into a Bluetooth LE HID keyboard+mouse+consumer peripheral
/// (HID-over-GATT). The iPad pairs to it directly — no phone, no dongle, no
/// driver swap — and Windows' normal Bluetooth keeps working. Exposes the
/// same Send* surface the input-capture layer used to call on the old network
/// client, so nothing above it had to change.
/// </summary>
public class BleHidPeripheral
{
    const int REPORT_KB = 1, REPORT_MOUSE = 2, REPORT_CONSUMER = 3;

    // Combined keyboard(1) + mouse(2) + consumer(3) report map.
    static readonly byte[] ReportMap =
    {
        0x05,0x01, 0x09,0x06, 0xA1,0x01, 0x85,0x01,
        0x05,0x07, 0x19,0xE0, 0x29,0xE7, 0x15,0x00, 0x25,0x01, 0x75,0x01, 0x95,0x08, 0x81,0x02,
        0x75,0x08, 0x95,0x01, 0x81,0x01,
        0x05,0x07, 0x19,0x00, 0x29,0x65, 0x15,0x00, 0x25,0x65, 0x75,0x08, 0x95,0x06, 0x81,0x00,
        0xC0,
        0x05,0x01, 0x09,0x02, 0xA1,0x01, 0x85,0x02,
        0x09,0x01, 0xA1,0x00,
        0x05,0x09, 0x19,0x01, 0x29,0x03, 0x15,0x00, 0x25,0x01, 0x95,0x03, 0x75,0x01, 0x81,0x02,
        0x95,0x01, 0x75,0x05, 0x81,0x01,
        0x05,0x01, 0x09,0x30, 0x09,0x31, 0x09,0x38, 0x15,0x81, 0x25,0x7F, 0x75,0x08, 0x95,0x03, 0x81,0x06,
        0xC0, 0xC0,
        0x05,0x0C, 0x09,0x01, 0xA1,0x01, 0x85,0x03,
        0x15,0x00, 0x26,0xFF,0x03, 0x19,0x00, 0x2A,0xFF,0x03, 0x75,0x10, 0x95,0x01, 0x81,0x00,
        0xC0
    };

    GattServiceProvider _hid;
    GattServiceProvider _battery;
    GattLocalCharacteristic _kbReport;
    GattLocalCharacteristic _mouseReport;
    GattLocalCharacteristic _consumerReport;

    // HID state lives here now (used to live in the Android service).
    readonly byte[] _keySlots = new byte[6];
    int _modifiers;
    int _mouseButtons;
    readonly object _lock = new();

    volatile bool _subscribed;
    public bool Connected => _subscribed;
    public event Action<bool> ConnectionChanged;

    static IBuffer Buf(params byte[] data)
    {
        var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    public async Task StartAsync()
    {
        var hidRes = await GattServiceProvider.CreateAsync(Uuid(0x1812));
        if (hidRes.Error != BluetoothError.Success)
            throw new InvalidOperationException($"HID service create failed: {hidRes.Error}");
        _hid = hidRes.ServiceProvider;

        await AddChar(_hid, 0x2A4A, GattCharacteristicProperties.Read,
            GattProtectionLevel.Plain, Buf(0x11, 0x01, 0x00, 0x03));                 // HID Information
        await AddChar(_hid, 0x2A4B, GattCharacteristicProperties.Read,
            GattProtectionLevel.EncryptionRequired, Buf(ReportMap));                 // Report Map
        await AddChar(_hid, 0x2A4C, GattCharacteristicProperties.WriteWithoutResponse,
            GattProtectionLevel.Plain, null);                                        // HID Control Point
        await AddChar(_hid, 0x2A4E,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
            GattProtectionLevel.Plain, Buf(0x01));                                   // Protocol Mode

        _kbReport = await AddInputReport(REPORT_KB);
        _mouseReport = await AddInputReport(REPORT_MOUSE);
        _consumerReport = await AddInputReport(REPORT_CONSUMER);

        var batRes = await GattServiceProvider.CreateAsync(Uuid(0x180F));
        _battery = batRes.ServiceProvider;
        await AddChar(_battery, 0x2A19,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            GattProtectionLevel.Plain, Buf(100));

        void OnSubs(GattLocalCharacteristic c, object _)
        {
            bool now = _kbReport.SubscribedClients.Count > 0 || _mouseReport.SubscribedClients.Count > 0;
            if (now != _subscribed)
            {
                _subscribed = now;
                if (!now) ResetState();
                ConnectionChanged?.Invoke(now);
            }
        }
        _kbReport.SubscribedClientsChanged += OnSubs;
        _mouseReport.SubscribedClientsChanged += OnSubs;

        var adv = new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true };
        _battery.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        _hid.StartAdvertising(adv);
    }

    static Guid Uuid(ushort id) => new($"0000{id:X4}-0000-1000-8000-00805f9b34fb");

    static async Task AddChar(GattServiceProvider svc, ushort uuid,
        GattCharacteristicProperties props, GattProtectionLevel read, IBuffer value)
    {
        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = props,
            ReadProtectionLevel = read
        };
        if (value != null) p.StaticValue = value;
        await svc.Service.CreateCharacteristicAsync(Uuid(uuid), p);
    }

    async Task<GattLocalCharacteristic> AddInputReport(byte reportId)
    {
        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
        };
        var cr = await _hid.Service.CreateCharacteristicAsync(Uuid(0x2A4D), p);
        await cr.Characteristic.CreateDescriptorAsync(Uuid(0x2908),
            new GattLocalDescriptorParameters { StaticValue = Buf(reportId, 0x01) });
        return cr.Characteristic;
    }

    void Notify(GattLocalCharacteristic ch, byte[] report)
    {
        if (!_subscribed) return;
        try { _ = ch.NotifyValueAsync(Buf(report)); } catch { }
    }

    void ResetState()
    {
        lock (_lock) { Array.Clear(_keySlots); _modifiers = 0; _mouseButtons = 0; }
    }

    // ---- Send surface (matches the old network client) ----

    public void SendMouseMove(int dx, int dy)
    {
        while (dx != 0 || dy != 0)
        {
            int sx = Math.Clamp(dx, -127, 127);
            int sy = Math.Clamp(dy, -127, 127);
            dx -= sx; dy -= sy;
            Notify(_mouseReport, new[] { (byte)_mouseButtons, (byte)(sbyte)sx, (byte)(sbyte)sy, (byte)0 });
        }
    }

    public void SendMouseButtons(int mask)
    {
        _mouseButtons = mask & 0x07;
        Notify(_mouseReport, new[] { (byte)_mouseButtons, (byte)0, (byte)0, (byte)0 });
    }

    public void SendScroll(int vertical, int horizontal)
    {
        Notify(_mouseReport, new[] { (byte)_mouseButtons, (byte)0, (byte)0, (byte)(sbyte)Math.Clamp(vertical, -127, 127) });
    }

    public void SendKey(byte usage, bool down)
    {
        lock (_lock)
        {
            if (usage >= 0xE0 && usage <= 0xE7)
            {
                int bit = 1 << (usage - 0xE0);
                _modifiers = down ? _modifiers | bit : _modifiers & ~bit;
            }
            else
            {
                if (down)
                {
                    if (Array.IndexOf(_keySlots, usage) < 0)
                    {
                        int free = Array.IndexOf(_keySlots, (byte)0);
                        if (free >= 0) _keySlots[free] = usage;
                    }
                }
                else
                {
                    for (int i = 0; i < 6; i++) if (_keySlots[i] == usage) _keySlots[i] = 0;
                }
            }
        }
        NotifyKeyboard();
    }

    public void SendConsumer(ushort usage, bool down)
    {
        int v = down ? usage : 0;
        Notify(_consumerReport, new[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) });
    }

    public void SendReleaseAll()
    {
        ResetState();
        NotifyKeyboard();
        Notify(_mouseReport, new byte[] { 0, 0, 0, 0 });
        Notify(_consumerReport, new byte[] { 0, 0 });
    }

    void NotifyKeyboard()
    {
        var r = new byte[8];
        lock (_lock) { r[0] = (byte)_modifiers; Array.Copy(_keySlots, 0, r, 2, 6); }
        Notify(_kbReport, r);
    }

    /// <summary>
    /// Presses a set of key usages together (e.g. Globe+H) then releases them.
    /// Globe is sent as Caps Lock (0x39); the iPad must have Caps Lock remapped
    /// to Globe in Settings. Used for forwarded touchpad gestures.
    /// </summary>
    public async void SendShortcut(params byte[] usages)
    {
        var report = new byte[8];
        int slot = 2;
        foreach (var u in usages)
        {
            if (u >= 0xE0 && u <= 0xE7) report[0] |= (byte)(1 << (u - 0xE0)); // real modifier → modifier byte
            else if (slot < 8) report[slot++] = u;                           // regular key (incl. CapsLock=Globe)
        }
        Notify(_kbReport, report);
        await Task.Delay(40);
        Notify(_kbReport, new byte[8]); // release all
        NotifyKeyboard();               // restore any physically-held keys (normally none in iPad mode)
    }
}
