using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IpadBridgeBle;

/// <summary>
/// Windows handles 3/4-finger touchpad swipes and taps in the shell, before
/// any app (or hook) can see them — they can't be intercepted, only turned
/// off. This temporarily zeroes the precision-touchpad gesture settings while
/// iPad mode is active and restores the originals afterwards. Originals are
/// stashed in the registry so a crash mid-mode is recovered on next startup.
/// </summary>
public static class GestureGuard
{
    const string PtpKeyPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    const string SaveKeyPath = @"Software\IpadBridge\SavedGestures";
    const int Absent = -1; // sentinel: value didn't exist (system default was in effect)

    static readonly string[] GestureValues =
    {
        "ThreeFingerSlideEnabled", "ThreeFingerTapEnabled",
        "FourFingerSlideEnabled", "FourFingerTapEnabled"
    };

    const int WM_SETTINGCHANGE = 0x001A;
    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);

    static void NudgeTouchpad()
    {
        // The touchpad input stack reads these on a settings-change broadcast;
        // without the nudge the change may not apply until re-login.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
            "PrecisionTouchPad", 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out _);
    }

    public static void Disable()
    {
        try
        {
            using var ptp = Registry.CurrentUser.CreateSubKey(PtpKeyPath);
            using var save = Registry.CurrentUser.CreateSubKey(SaveKeyPath);
            foreach (string name in GestureValues)
            {
                if (save.GetValue(name) == null) // keep the oldest save if already stashed
                    save.SetValue(name, ptp.GetValue(name) ?? Absent, RegistryValueKind.DWord);
                ptp.SetValue(name, 0, RegistryValueKind.DWord);
            }
            NudgeTouchpad();
        }
        catch
        {
            // gesture suppression is best-effort; never break input forwarding
        }
    }

    /// <summary>Restore saved gesture settings. Also called at startup for crash recovery.</summary>
    public static void Restore()
    {
        try
        {
            using var save = Registry.CurrentUser.OpenSubKey(SaveKeyPath, writable: true);
            if (save == null) return;
            using var ptp = Registry.CurrentUser.CreateSubKey(PtpKeyPath);
            foreach (string name in GestureValues)
            {
                object saved = save.GetValue(name);
                if (saved == null) continue;
                int value = Convert.ToInt32(saved);
                if (value == Absent) ptp.DeleteValue(name, throwOnMissingValue: false);
                else ptp.SetValue(name, value, RegistryValueKind.DWord);
                save.DeleteValue(name, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKey(SaveKeyPath, throwOnMissingSubKey: false);
            NudgeTouchpad();
        }
        catch
        {
            // best-effort
        }
    }
}
