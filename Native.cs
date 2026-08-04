using System.Runtime.InteropServices;

namespace HtmlWallpaper;

/// <summary>
/// Win32 interop and the "WorkerW" desktop-parenting technique used to render
/// a window behind the desktop icons (native live wallpaper).
/// </summary>
internal static class Native
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080; // keeps window out of Alt-Tab
    internal const int WS_EX_LAYERED = 0x00080000;    // required for DWM compositing behind icons
    internal const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000; // marks the "raised" desktop
    internal const uint WM_SPAWN_WORKERW = 0x052C;
    internal const uint LWA_ALPHA = 0x2;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_RESTORE = 9;

    internal static readonly IntPtr HWND_BOTTOM = new(1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Ask the shell to create the WorkerW wallpaper layer and return the surface to
    /// parent the wallpaper window to, so it renders below the desktop icons.
    ///
    /// Modern Windows 11 uses a "raised desktop with layered ShellView": Progman has
    /// WS_EX_NOREDIRECTIONBITMAP, SHELLDLL_DefView is a layered child of Progman, and
    /// the wallpaper is drawn by a WorkerW child of Progman z-ordered *under* DefView.
    /// The message must be sent with wParam=0xD, lParam=0x1 to spawn that child WorkerW.
    /// The caller must give its window WS_EX_LAYERED so DWM composites it correctly.
    ///
    /// On classic layouts we fall back to the sibling-WorkerW technique, and finally
    /// to Progman itself.
    /// </summary>
    internal static IntPtr GetWallpaperWorkerW()
    {
        IntPtr progman = FindWindow("Progman", null);
        bool raised = (GetWindowLong(progman, GWL_EXSTYLE) & WS_EX_NOREDIRECTIONBITMAP) != 0;

        // Look for an existing wallpaper layer first. Re-sending WM_SPAWN_WORKERW when a
        // layer already exists forces Windows to tear down and repaint the whole desktop
        // wallpaper, which shows up as a periodic flash. Only spawn if it's missing.
        IntPtr workerw = FindWallpaperLayer(progman, raised);

        if (workerw == IntPtr.Zero)
        {
            SendMessageTimeout(progman, WM_SPAWN_WORKERW, new IntPtr(0xD), new IntPtr(0x1), 0x0000, 1000, out _);
            workerw = FindWallpaperLayer(progman, raised);
        }

        return workerw != IntPtr.Zero ? workerw : progman;
    }

    /// <summary>Locate the existing wallpaper layer without asking the shell to spawn one.</summary>
    private static IntPtr FindWallpaperLayer(IntPtr progman, bool raised)
    {
        if (raised)
        {
            // The wallpaper WorkerW is a child of Progman, z-ordered under the icons.
            return FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        }

        // Classic: the wallpaper layer is the WorkerW that follows, in z-order, the
        // top-level window hosting SHELLDLL_DefView.
        IntPtr found = IntPtr.Zero;
        EnumWindows((tophandle, _) =>
        {
            if (FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                IntPtr sibling = FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
                if (sibling != IntPtr.Zero)
                    found = sibling;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
