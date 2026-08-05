using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HtmlWallpaper.Tray;

/// <summary>
/// A message-only window that owns a single global hotkey and raises an event on
/// the UI thread when it is pressed. Registering against this window means
/// WM_HOTKEY is delivered to our WndProc on the message-loop thread.
/// </summary>
internal sealed class GlobalHotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4B1D;

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyWindow()
    {
        var cp = new CreateParams { Parent = (IntPtr)(-3) }; // HWND_MESSAGE
        CreateHandle(cp);
    }

    public bool Register(Hotkey hk)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        return RegisterHotKey(Handle, HOTKEY_ID, hk.Modifiers, hk.Vk);
    }

    public void Unregister() => UnregisterHotKey(Handle, HOTKEY_ID);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) HotkeyPressed?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
