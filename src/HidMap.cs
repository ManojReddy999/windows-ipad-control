namespace IpadBridgeBle;

/// <summary>Windows virtual-key codes to USB HID keyboard usage IDs (usage page 0x07).</summary>
public static class HidMap
{
    // HID modifier usages
    public const byte LCtrl = 0xE0, LShift = 0xE1, LAlt = 0xE2, LGui = 0xE3;
    public const byte RCtrl = 0xE4, RShift = 0xE5, RAlt = 0xE6, RGui = 0xE7;

    // HID consumer usages (usage page 0x0C)
    public const ushort ConsumerPlayPause = 0x00CD;
    public const ushort ConsumerScanNext = 0x00B5;
    public const ushort ConsumerScanPrev = 0x00B6;
    public const ushort ConsumerVolumeUp = 0x00E9;
    public const ushort ConsumerVolumeDown = 0x00EA;
    public const ushort ConsumerMute = 0x00E2;

    static readonly Dictionary<int, byte> Map = new();

    static HidMap()
    {
        // Letters A-Z
        for (int vk = 0x41; vk <= 0x5A; vk++) Map[vk] = (byte)(0x04 + (vk - 0x41));
        // Digits 1-9, 0
        for (int vk = 0x31; vk <= 0x39; vk++) Map[vk] = (byte)(0x1E + (vk - 0x31));
        Map[0x30] = 0x27;

        Map[0x0D] = 0x28; // Enter
        Map[0x1B] = 0x29; // Escape
        Map[0x08] = 0x2A; // Backspace
        Map[0x09] = 0x2B; // Tab
        Map[0x20] = 0x2C; // Space

        Map[0xBD] = 0x2D; // - _
        Map[0xBB] = 0x2E; // = +
        Map[0xDB] = 0x2F; // [ {
        Map[0xDD] = 0x30; // ] }
        Map[0xDC] = 0x31; // \ |
        Map[0xBA] = 0x33; // ; :
        Map[0xDE] = 0x34; // ' "
        Map[0xC0] = 0x35; // ` ~
        Map[0xBC] = 0x36; // , <
        Map[0xBE] = 0x37; // . >
        Map[0xBF] = 0x38; // / ?

        Map[0x14] = 0x39; // Caps Lock

        // F1-F12
        for (int vk = 0x70; vk <= 0x7B; vk++) Map[vk] = (byte)(0x3A + (vk - 0x70));

        Map[0x2C] = 0x46; // Print Screen
        Map[0x91] = 0x47; // Scroll Lock
        Map[0x13] = 0x48; // Pause

        Map[0x2D] = 0x49; // Insert
        Map[0x24] = 0x4A; // Home
        Map[0x21] = 0x4B; // Page Up
        Map[0x2E] = 0x4C; // Delete
        Map[0x23] = 0x4D; // End
        Map[0x22] = 0x4E; // Page Down

        Map[0x27] = 0x4F; // Right arrow
        Map[0x25] = 0x50; // Left arrow
        Map[0x28] = 0x51; // Down arrow
        Map[0x26] = 0x52; // Up arrow

        Map[0x90] = 0x53; // Num Lock
        Map[0x6F] = 0x54; // Numpad /
        Map[0x6A] = 0x55; // Numpad *
        Map[0x6D] = 0x56; // Numpad -
        Map[0x6B] = 0x57; // Numpad +
        Map[0x60] = 0x62; // Numpad 0
        for (int vk = 0x61; vk <= 0x69; vk++) Map[vk] = (byte)(0x59 + (vk - 0x61)); // Numpad 1-9
        Map[0x6E] = 0x63; // Numpad .

        // Modifiers (low-level hooks always deliver side-specific VKs)
        Map[0xA2] = LCtrl;
        Map[0xA0] = LShift;
        Map[0xA4] = LAlt;
        Map[0x5B] = LGui;  // Left Win
        Map[0xA3] = RCtrl;
        Map[0xA1] = RShift;
        Map[0xA5] = RAlt;
        Map[0x5C] = RGui;  // Right Win
    }

    /// <summary>Returns the HID usage for a VK code, or 0 if unmapped.</summary>
    public static byte ToUsage(int vkCode, bool mapCtrlToCmd)
    {
        if (!Map.TryGetValue(vkCode, out byte usage)) return 0;
        if (mapCtrlToCmd)
        {
            usage = usage switch
            {
                LCtrl => LGui, // Ctrl acts as Cmd
                RCtrl => RGui,
                LGui => LCtrl, // Win acts as Ctrl
                RGui => RCtrl,
                _ => usage
            };
        }
        return usage;
    }
}
