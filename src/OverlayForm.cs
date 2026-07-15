namespace IpadBridgeBle;

/// <summary>
/// Nearly-invisible fullscreen window shown while iPad mode is active.
/// Precision-touchpad two-finger scrolling bypasses low-level mouse hooks
/// (Windows synthesizes smooth-scroll input directly to the window under the
/// cursor), so this overlay sits under the parked cursor to catch it. It also
/// hides the cursor and blocks stray interaction with the desktop.
/// </summary>
public class OverlayForm : Form
{
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>Raw wheel delta (multiples/fractions of 120), horizontal flag.</summary>
    public event Action<int, bool> WheelDelta;

    /// <summary>Fallback exit (only reachable if the keyboard hook died).</summary>
    public event Action ExitRequested;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.02; // imperceptible, but still hit-testable and scrollable
        KeyPreview = true;
    }

    public void ShowOverlay()
    {
        Bounds = SystemInformation.VirtualScreen;
        Show();
        Activate();
        Cursor.Hide();
    }

    public void HideOverlay()
    {
        if (!Visible) return;
        Cursor.Show();
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.Alt && e.KeyCode == Keys.I)
        {
            ExitRequested?.Invoke();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL)
        {
            int delta = unchecked((short)((ulong)m.WParam >> 16));
            WheelDelta?.Invoke(delta, m.Msg == WM_MOUSEHWHEEL);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Never die to Alt+F4 etc. while in iPad mode; TrayApp owns lifetime.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            ExitRequested?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }
}
