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
    private readonly bool _spanAllMonitors;
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 3000 };
    private IntPtr _desktopLayer = IntPtr.Zero;

    public WallpaperForm(string source, bool spanAllMonitors)
    {
        _source = source;
        _spanAllMonitors = spanAllMonitors;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ControlBox = false;
        Text = "HtmlWallpaper";

        Rectangle area = spanAllMonitors
            ? SystemInformation.VirtualScreen
            : Screen.PrimaryScreen!.Bounds;

        Bounds = area;

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
            "HtmlWallpaper", "WebView2");
        Directory.CreateDirectory(userData);

        // Allow media to autoplay without a user gesture (muted by default).
        var options = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: "--autoplay-policy=no-user-gesture-required");

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

        // Re-attach only if the shell actually detached us (e.g., Explorer restart).
        // We must NOT re-run the attach on every tick: re-sending the spawn message and
        // re-parenting an already-attached window causes a visible periodic flash.
        _watchdog.Tick += (_, _) =>
        {
            IntPtr parent = Native.GetParent(Handle);
            bool stillAttached = parent != IntPtr.Zero &&
                                 (parent == _desktopLayer || Native.IsWindow(_desktopLayer));
            if (!stillAttached)
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

        // After re-parenting, coordinates are relative to the desktop layer, whose
        // origin aligns with the primary monitor's top-left (0,0). Re-apply bounds so
        // monitors positioned left/above primary (negative coords) render correctly.
        Rectangle area = _spanAllMonitors
            ? SystemInformation.VirtualScreen
            : Screen.PrimaryScreen!.Bounds;
        Location = area.Location;
        Size = area.Size;
    }
}
