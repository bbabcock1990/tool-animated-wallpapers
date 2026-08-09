using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HtmlWallpaper;

/// <summary>
/// A transparent, top-most, non-activating window rendered *in front of* the desktop
/// that hosts the same modules as the wallpaper, but is interactive: its clickable
/// hit-region is clipped to only the module panels (everything else passes straight
/// through to the desktop icons), and links inside a panel open in the browser —
/// except Outlook calendar links, which open the "new" Outlook desktop app when it
/// is installed (falling back to the browser otherwise).
///
/// Why this exists: the wallpaper window is parented behind the desktop icons
/// (SHELLDLL_DefView), so Windows routes every desktop click to the icon layer and the
/// wallpaper can never receive a click. This overlay is the opposite — a normal
/// top-level window — but it uses SetWindowRgn so it only *exists* where the panels are,
/// leaving the rest of the desktop fully usable.
///
/// Visibility is "desktop only": it is shown while the desktop (Progman/WorkerW) is the
/// foreground window and hidden the moment a normal application is focused, so it never
/// covers your apps.
///
/// Modules opt in to interactivity by tagging their DOM:
///   - the panel root element carries  data-wp-panel
///   - any clickable element carries    data-wp-href="https://…"
/// The generic front-end bridge (interactive.js) measures the panels and routes clicks;
/// this class owns the Win32 side (region + foreground gating + launching URLs).
/// </summary>
internal sealed class InteractiveOverlayForm : Form
{
    private readonly WebView2 _webView = new();
    private readonly string _source;
    private readonly Rectangle _targetBounds;
    private readonly string _userDataSubfolder;
    private bool _ready;
    private bool _wantVisible;   // requested by the host (clickable mode on, not hidden)

    public InteractiveOverlayForm(string source, Rectangle targetBounds, string userDataSubfolder)
    {
        _source = source;
        _targetBounds = targetBounds;
        _userDataSubfolder = userDataSubfolder;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        TransparencyKey = Color.Black; // black pixels become fully click/paint-transparent
        ControlBox = false;
        Text = "HtmlWallpaperOverlay";
        Bounds = _targetBounds;

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.Transparent;
        Controls.Add(_webView);

        Load += OnLoadAsync;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Out of Alt-Tab, layered (for TransparencyKey), always top-most, and
            // never steal focus — clicking a panel must not change the foreground
            // window, so the app you were using keeps its focus.
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED
                        | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Show or hide the clickable overlay. Called by the host when the user toggles
    /// "clickable mode" (or hides all widgets). The window starts hidden.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (!_ready) return;
        if (_wantVisible)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        // Start with an empty hit-region so nothing is clickable until the page
        // reports where its panels are.
        SetEmptyRegion();

        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HtmlWallpaper", "WebView2", _userDataSubfolder);
        Directory.CreateDirectory(userData);

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

        core.WebMessageReceived += OnWebMessage;

        Navigate(_source);
        _ready = true;

        // Keep it top-most without activating, then apply the requested visibility
        // (hidden until the user turns on clickable mode).
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        ApplyVisibility();
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? (t.GetString() ?? "") : "";

            if (type == "regions" && root.TryGetProperty("rects", out JsonElement rects) &&
                rects.ValueKind == JsonValueKind.Array)
            {
                ApplyRegion(rects);
            }
            else if (type == "open" && root.TryGetProperty("url", out JsonElement u))
            {
                OpenUrl(u.GetString());
            }
        }
        catch { /* ignore malformed messages */ }
    }

    /// <summary>Clip the window so only the reported panel rectangles are "solid".</summary>
    private void ApplyRegion(JsonElement rects)
    {
        IntPtr union = IntPtr.Zero;
        foreach (JsonElement r in rects.EnumerateArray())
        {
            int x = GetInt(r, "x");
            int y = GetInt(r, "y");
            int w = GetInt(r, "w");
            int h = GetInt(r, "h");
            if (w <= 0 || h <= 0) continue;

            IntPtr rgn = Native.CreateRectRgn(x, y, x + w, y + h);
            if (union == IntPtr.Zero)
            {
                union = rgn;
            }
            else
            {
                Native.CombineRgn(union, union, rgn, Native.RGN_OR);
                Native.DeleteObject(rgn);
            }
        }

        if (union == IntPtr.Zero)
        {
            SetEmptyRegion();
            return;
        }

        // SetWindowRgn takes ownership of the region handle on success.
        Native.SetWindowRgn(Handle, union, true);
    }

    private void SetEmptyRegion()
    {
        IntPtr empty = Native.CreateRectRgn(0, 0, 0, 0);
        Native.SetWindowRgn(Handle, empty, true);
    }

    private static int GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : 0;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        // Outlook calendar links: prefer the installed "new" Outlook desktop app
        // (ms-outlook: — never classic Outlook), and only fall back to opening the
        // web link in the browser when that app isn't available. The new Outlook
        // has no supported deep link to a specific event, so it opens the app; the
        // web fallback still lands on the exact meeting.
        if (IsOutlookCalendarLink(uri) && TryOpenInNewOutlook())
            return;

        OpenInBrowser(uri.AbsoluteUri);
    }

    /// <summary>True for an Outlook/OWA calendar-item URL (e.g. an event's Graph webLink).</summary>
    private static bool IsOutlookCalendarLink(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        bool outlookHost =
            host.EndsWith("outlook.office.com") ||
            host.EndsWith("outlook.office365.com") ||
            host.EndsWith("outlook.live.com");
        if (!outlookHost) return false;

        string where = (uri.AbsolutePath + " " + uri.Query).ToLowerInvariant();
        return where.Contains("itemid") || where.Contains("/calendar");
    }

    /// <summary>
    /// Launch the "new" Outlook for Windows via its <c>ms-outlook:</c> protocol.
    /// Returns false (so the caller can fall back to the browser) if no handler is
    /// registered — i.e. the new Outlook isn't installed.
    /// </summary>
    private static bool TryOpenInNewOutlook()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-outlook:",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* best effort */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
