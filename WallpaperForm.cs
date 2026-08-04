using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HtmlWallpaper;

/// <summary>
/// A borderless, click-through-to-desktop window that hosts a WebView2 control
/// and is parented behind the desktop icons via the WorkerW technique.
/// </summary>
internal sealed class WallpaperForm : Form
{
    private readonly WebView2 _webView = new();
    private readonly string _source;
    private readonly Rectangle _targetBounds;
    private readonly string _userDataSubfolder;
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 3000 };
    private IntPtr _desktopLayer = IntPtr.Zero;

    /// <param name="targetBounds">The monitor rectangle (screen coordinates) this window covers.</param>
    /// <param name="userDataSubfolder">Per-window WebView2 user-data subfolder (must be unique per window in the process).</param>
    public WallpaperForm(string source, Rectangle targetBounds, string userDataSubfolder)
    {
        _source = source;
        _targetBounds = targetBounds;
        _userDataSubfolder = userDataSubfolder;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ControlBox = false;
        Text = "HtmlWallpaper";

        Bounds = _targetBounds;

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.Black;
        Controls.Add(_webView);

        Load += OnLoadAsync;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_TOOLWINDOW keeps it out of Alt-Tab; WS_EX_LAYERED is required so
            // DWM composites the window correctly when hosted under the desktop icons
            // on the modern "raised desktop" (otherwise it renders solid black).
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            return cp;
        }
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HtmlWallpaper", "WebView2", _userDataSubfolder);
        Directory.CreateDirectory(userData);

        // Browser flags tuned for the wallpaper scenario:
        //  - autoplay-policy: let media start without a user gesture.
        //  - CalculateNativeWinOcclusion (disabled): our window is parented under the
        //    desktop icons, so Chromium's occlusion detection keeps deciding it is
        //    "hidden" and pausing/resuming painting — that is the periodic flicker.
        //  - backgrounding/timer-throttling (disabled): keep rendering at full rate
        //    even though the window is never the foreground window.
        var options = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments:
                "--autoplay-policy=no-user-gesture-required " +
                "--disable-features=CalculateNativeWinOcclusion " +
                "--disable-backgrounding-occluded-windows " +
                "--disable-renderer-backgrounding " +
                "--disable-background-timer-throttling");

        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: userData, options: options);

        await _webView.EnsureCoreWebView2Async(env);

        CoreWebView2 core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        Navigate(_source);

        AttachToDesktop();

        // Only re-attach when the desktop layer we parented to is actually gone
        // (Explorer restart). GetParent() is unreliable here: for a top-level window
        // re-parented under the Windows 11 "raised desktop", it returns 0 even while we
        // are correctly attached — so using it caused a re-parent every tick, which
        // produces a periodic GPU-compositor flicker on the WebView2 (DirectComposition)
        // surface that a screenshot cannot even capture.
        _watchdog.Tick += (_, _) =>
        {
            if (!Native.IsWindow(_desktopLayer))
                AttachToDesktop();
        };
        _watchdog.Start();
    }

    private void Navigate(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            _webView.CoreWebView2.Navigate(source);
        }
        else
        {
            string full = Path.GetFullPath(source);
            _webView.CoreWebView2.Navigate(new Uri(full).AbsoluteUri);
        }
    }

    /// <summary>Parent this form to the desktop wallpaper layer (below the icons).</summary>
    private void AttachToDesktop()
    {
        // Make the layered window fully opaque so DWM blts our content 1:1. This is
        // what allows a window hosted under the desktop icons to render at all.
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);

        IntPtr desktopLayer = Native.GetWallpaperWorkerW();
        _desktopLayer = desktopLayer;
        Native.SetParent(Handle, desktopLayer);

        // After re-parenting, child coordinates are relative to the desktop layer's
        // client origin. That origin is NOT necessarily the primary monitor's (0,0):
        // on multi-monitor setups the WorkerW covers the whole virtual screen, so its
        // top-left in screen coordinates is the virtual-screen origin (which may be
        // negative when a monitor sits left of / above primary). Query it directly and
        // translate this window's target monitor rectangle into that coordinate space,
        // so each per-monitor window lands on the right physical display.
        int offsetX = 0, offsetY = 0;
        if (Native.GetWindowRect(desktopLayer, out Native.RECT wr))
        {
            offsetX = wr.Left;
            offsetY = wr.Top;
        }
        Location = new Point(_targetBounds.X - offsetX, _targetBounds.Y - offsetY);
        Size = _targetBounds.Size;
    }
}
