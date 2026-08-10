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
    private bool _ready;
    private bool _widgetsHidden;
    private bool _clickMode;

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
      try
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

        // A dock/undock rebuild can close this window while the awaits above are in
        // flight; touching the disposed control would throw. Bail out quietly.
        if (IsDisposed || Disposing || _webView.IsDisposed) return;

        await _webView.EnsureCoreWebView2Async(env);

        if (IsDisposed || Disposing || _webView.IsDisposed || _webView.CoreWebView2 is null) return;

        CoreWebView2 core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        Navigate(_source);

        AttachToDesktop();

        _ready = true;
        ApplyWidgetsHidden();
        ApplyClickMode();

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
      catch (Exception ex)
      {
          // Most likely a WebView2 create/close race during a display-change rebuild.
          // The window will be recreated by the next Build; never crash the process.
          Program.Log("WallpaperForm.OnLoadAsync", ex);
      }
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

    /// <summary>
    /// Hide or show this window's module panels (the "widgets"). Used when the user
    /// turns on clickable mode (the ambient panels are hidden so the interactive
    /// overlay is the sole renderer — no ghosting) or hides all widgets. The base
    /// animation/clock are unaffected. Panels opt in by carrying data-wp-panel;
    /// module-loader.js owns the CSS that reacts to the html[data-wp-hidden] flag.
    /// </summary>
    public void SetWidgetsHidden(bool hidden)
    {
        _widgetsHidden = hidden;
        ApplyWidgetsHidden();
    }

    private void ApplyWidgetsHidden()
    {
        if (!_ready) return;
        try
        {
            string on = _widgetsHidden ? "true" : "false";
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"document.documentElement.toggleAttribute('data-wp-hidden', {on});");
        }
        catch { /* best effort — the panel simply stays as-is */ }
    }

    /// <summary>
    /// Enter/leave "clickable mode". When on, the ambient copies of *interactive*
    /// panels (those with a link) are hidden because the front interactive overlay
    /// renders them instead — this prevents a ghosted double image. Non-interactive
    /// panels (e.g. the calendar) are untouched.
    /// </summary>
    public void SetClickMode(bool on)
    {
        _clickMode = on;
        ApplyClickMode();
    }

    private void ApplyClickMode()
    {
        if (!_ready) return;
        try
        {
            string on = _clickMode ? "true" : "false";
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"document.documentElement.toggleAttribute('data-wp-clickmode', {on});");
        }
        catch { /* best effort */ }
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
