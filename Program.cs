using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HtmlWallpaper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Module management CLI: HtmlWallpaper.exe module <list|enable|disable|refresh|add|registry> ...
        if (ModuleCli.IsModuleCommand(args))
            return ModuleCli.Run(args);

        // Management CLI: HtmlWallpaper.exe <set|stop|autostart> ... (folds in the
        // old Set-Wallpaper / Stop-Wallpaper / Enable-Startup / Disable-Startup scripts).
        if (WallpaperCli.IsCommand(args))
            return WallpaperCli.Run(args);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            MessageBox.Show(
                "HtmlWallpaper — render an HTML file or URL as your Windows desktop background.\n\n" +
                "Render:\n" +
                "  HtmlWallpaper.exe <path-to-html | url> [--primary | --monitor N]\n" +
                "  --primary    Cover only the primary monitor.\n" +
                "  --monitor N  Cover only monitor index N (0-based).\n\n" +
                "Manage:\n" +
                "  HtmlWallpaper.exe set <file|url> [--primary | --monitor N]   Set the wallpaper.\n" +
                "  HtmlWallpaper.exe stop                                       Stop it, restore desktop.\n" +
                "  HtmlWallpaper.exe autostart on --source <file|url>           Run at login.\n" +
                "  HtmlWallpaper.exe autostart off                             Remove login entry.\n" +
                "  HtmlWallpaper.exe module list                               Manage modules.",
                "HtmlWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        string source = args[0];

        // Ensure only one wallpaper instance is running.
        foreach (Process p in Process.GetProcessesByName("HtmlWallpaper"))
        {
            if (p.Id != Environment.ProcessId)
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { /* ignore */ }
            }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Safety net: keep transient failures — most importantly the WebView2
        // create/close races that happen when a dock/undock triggers a rebuild —
        // from tearing down the wallpaper with the .NET "Unhandled exception"
        // dialog (Operation aborted / E_ABORT). Log them and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Decide which monitors to cover, and keep them correct as displays change.
        Application.Run(new MultiFormContext(source, args));
        return 0;
    }

    /// <summary>
    /// Append a diagnostic line to %LOCALAPPDATA%\HtmlWallpaper\wallpaper.log. Used by
    /// the global exception handlers so a swallowed crash still leaves a trace. Must
    /// never throw.
    /// </summary>
    internal static void Log(string kind, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HtmlWallpaper");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "wallpaper.log"),
                $"{DateTime.Now:o} [{kind}] {ex}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }

    internal static List<Rectangle> ResolveTargetScreens(string[] args)
    {
        // Enumerate monitors live from the OS (not the cached Screen.AllScreens, which
        // can stay stale across a dock/undock when the display-change notification is
        // not delivered to this process — the root cause of the wallpaper not expanding
        // onto newly connected monitors).
        List<(Rectangle Bounds, bool Primary)> monitors = Native.EnumerateMonitors();

        // Fall back to WinForms only in the unlikely event the OS enumeration is empty.
        if (monitors.Count == 0)
            monitors = Screen.AllScreens.Select(s => (s.Bounds, s.Primary)).ToList();

        int monitorFlag = Array.FindIndex(args, a =>
            string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase));
        if (monitorFlag >= 0 && monitorFlag + 1 < args.Length &&
            int.TryParse(args[monitorFlag + 1], out int idx) &&
            idx >= 0 && idx < monitors.Count)
        {
            return new List<Rectangle> { monitors[idx].Bounds };
        }

        if (args.Contains("--primary", StringComparer.OrdinalIgnoreCase))
        {
            (Rectangle Bounds, bool Primary) primary =
                monitors.FirstOrDefault(m => m.Primary, monitors[0]);
            return new List<Rectangle> { primary.Bounds };
        }

        return monitors.Select(m => m.Bounds).ToList();
    }
}

/// <summary>
/// Hosts one wallpaper window per monitor and rebuilds them automatically when the
/// display configuration changes (dock/undock, monitor added/removed, resolution or
/// layout change), so the wallpaper stays correct without a restart.
/// </summary>
internal sealed class MultiFormContext : ApplicationContext
{
    private readonly string _source;
    private readonly string[] _args;
    private readonly List<WallpaperForm> _forms = new();
    // Authoritative display-change detection. The previous implementation used
    // System.Windows.Forms.Timer for this, but WM_TIMER delivery on the UI thread
    // was observed to stop after some dock/undock and resume-from-sleep transitions,
    // leaving the wallpaper stuck on the old monitor layout. A thread-pool timer is
    // independent of the UI message pump and keeps polling reliably (this is the same
    // mechanism the module scheduler uses). The rebuild itself is marshaled back onto
    // the UI thread via _sync, since WinForms windows must be created/closed there.
    private System.Threading.Timer? _poll;
    private readonly Form _sync = new() { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
    private volatile bool _rebuilding;
    private volatile bool _rebuildRequested;
    private volatile string _builtSignature = "";
    private string _pendingSignature = "";
    private int _generation;

    // Optional interactive overlay (clickable panels in front of the desktop). One
    // window on the primary monitor; recreated on display changes alongside the
    // wallpaper windows. Null when the interactive page isn't present.
    private InteractiveOverlayForm? _overlay;

    // Engine-level global hotkeys:
    //   _hkClickable — toggle "clickable mode": show the interactive overlay and
    //                  hide the ambient copies of interactive panels.
    //   _hkHide      — hide/show all widgets (module panels), leaving the animation.
    // The combos are user-configurable in the tray's Settings dialog (persisted in
    // state.json); the tray raises HotkeysChanged and we re-register here.
    private Tray.GlobalHotkeyWindow? _hkClickable;
    private Tray.GlobalHotkeyWindow? _hkHide;
    private bool _clickable;
    private bool _widgetsHidden;

    // Module runtime (tray + in-process data scheduler), owned by the wallpaper
    // process so there is no separate tray process or scheduled task.
    private readonly Modules.ModuleService _moduleService = new();
    private Modules.ModuleScheduler? _scheduler;
    private Tray.ModuleTray? _tray;

    public MultiFormContext(string source, string[] args)
    {
        _source = source;
        _args = args;

        // Force the hidden marshaling window's handle to be created now, on the UI
        // thread, so the background poll timer can BeginInvoke the rebuild onto it.
        _ = _sync.Handle;

        // Fast-path hint. This can fire before the poll notices, but it is NOT relied
        // upon — it does not always arrive on dock/undock or resume-from-sleep, which
        // is why the thread-pool poll below is the authoritative detector.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Build();

        // Start the module runtime once, after the UI thread/message loop exists.
        StartModuleRuntime();

        // Register the engine hotkeys once (they drive whatever windows exist now
        // and after any rebuild).
        StartOverlayHotkeys();

        // Poll the live monitor layout every second on a thread-pool timer and rebuild
        // when it drifts from what we drew. Independent of the UI message pump.
        _poll = new System.Threading.Timer(_ => Poll(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StartOverlayHotkeys()
    {
        try
        {
            _hkClickable = new Tray.GlobalHotkeyWindow();
            _hkClickable.HotkeyPressed += (_, _) => { _clickable = !_clickable; ApplyOverlayState(); };

            _hkHide = new Tray.GlobalHotkeyWindow();
            _hkHide.HotkeyPressed += (_, _) => { _widgetsHidden = !_widgetsHidden; ApplyOverlayState(); };

            RegisterOverlayHotkeys();

            // Re-register whenever the user changes them in the Settings dialog.
            if (_tray != null) _tray.HotkeysChanged += RegisterOverlayHotkeys;
        }
        catch { /* hotkeys are a convenience; the wallpaper still runs without them */ }
    }

    /// <summary>Register (or re-register) the two engine hotkeys from the tray's current values.</summary>
    private void RegisterOverlayHotkeys()
    {
        try
        {
            Tray.Hotkey click = _tray?.ClickHotkey ?? Tray.ModuleTray.DefaultClickHotkey;
            Tray.Hotkey hide = _tray?.HideHotkey ?? Tray.ModuleTray.DefaultHideHotkey;
            _hkClickable?.Register(click);
            _hkHide?.Register(hide);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Reconcile the interactive overlay + ambient panels with the two toggles:
    ///   - clickable ON  => overlay shown, ambient panels hidden (overlay is the sole
    ///                       renderer, so there is no double image).
    ///   - clickable OFF => overlay hidden, ambient panels shown (glanceable).
    ///   - hide-all      => everything hidden regardless of clickable.
    /// Safe to call after every Build/Rebuild so a display change preserves state.
    /// </summary>
    private void ApplyOverlayState()
    {
        bool showOverlay = _clickable && !_widgetsHidden;

        _overlay?.SetVisible(showOverlay);
        foreach (WallpaperForm form in _forms)
        {
            // Hide *all* panels when hiding widgets; otherwise, in clickable mode hide
            // only the interactive panels the overlay is taking over (no ghosting).
            form.SetWidgetsHidden(_widgetsHidden);
            form.SetClickMode(showOverlay);
        }
    }

    private void StartModuleRuntime()
    {
        try
        {
            // Ensure the browser-readable registry reflects the current state.
            _moduleService.Registry.WriteRegistry();
            _tray = new Tray.ModuleTray(_moduleService);
            _scheduler = new Modules.ModuleScheduler(_moduleService);
        }
        catch { /* the wallpaper must still run even if the module runtime fails */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _poll?.Dispose();
            _scheduler?.Dispose();
            _tray?.Dispose();
            _hkClickable?.Dispose();
            _hkHide?.Dispose();
            _overlay?.Dispose();
            _sync?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RequestRebuild();

    /// <summary>
    /// Runs on a thread-pool thread. Compares the live monitor layout to what we drew
    /// and requests a rebuild once a changed layout has been stable across two polls
    /// (so we don't rebuild repeatedly while Windows is still adding/removing monitors).
    /// </summary>
    private void Poll()
    {
        if (_rebuilding || _rebuildRequested) return;

        string current;
        try { current = Signature(Program.ResolveTargetScreens(_args)); }
        catch { return; }

        if (current == _builtSignature) { _pendingSignature = ""; return; }

        if (current == _pendingSignature) RequestRebuild();
        else _pendingSignature = current;
    }

    /// <summary>Marshal a rebuild onto the UI thread; WinForms windows must live there.</summary>
    private void RequestRebuild()
    {
        if (_rebuildRequested) return;
        _rebuildRequested = true;
        try
        {
            if (_sync.IsHandleCreated && !_sync.IsDisposed)
                _sync.BeginInvoke(new Action(Rebuild));
            else
                _rebuildRequested = false;
        }
        catch { _rebuildRequested = false; }
    }

    private static string Signature(List<Rectangle> targets) =>
        string.Join(";", targets.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));

    private void Build()
    {
        int gen = _generation++;
        List<Rectangle> targets = Program.ResolveTargetScreens(_args);
        _builtSignature = Signature(targets);
        for (int i = 0; i < targets.Count; i++)
        {
            var form = new WallpaperForm(_source, targets[i], $"g{gen}_mon{i}");
            form.FormClosed += (_, _) =>
            {
                _forms.Remove(form);
                // If every window is gone and we're not mid-rebuild, exit the app.
                if (!_rebuilding && _forms.Count == 0)
                    ExitThread();
            };
            _forms.Add(form);
            form.Show();
        }

        BuildOverlay(gen, targets);

        // Re-apply clickable / hidden state to the freshly built windows so a display
        // change (rebuild) doesn't reset it.
        ApplyOverlayState();
    }

    /// <summary>
    /// Create the interactive overlay on the primary monitor, if the interactive page
    /// exists next to the wallpaper. Loads the same module runtime, but clickable.
    /// </summary>
    private void BuildOverlay(int gen, List<Rectangle> targets)
    {
        try
        {
            string interactivePage = Path.GetFullPath("overlay-interactive.html");
            if (!File.Exists(interactivePage)) return;

            // Choose the primary monitor's rectangle (fall back to the first target).
            List<(Rectangle Bounds, bool Primary)> monitors = Native.EnumerateMonitors();
            Rectangle primary = monitors.Count > 0
                ? monitors.FirstOrDefault(m => m.Primary, monitors[0]).Bounds
                : targets[0];
            if (!targets.Any(t => t == primary)) primary = targets[0];

            _overlay = new InteractiveOverlayForm(interactivePage, primary, $"overlay_g{gen}");
            _overlay.FormClosed += (_, _) => { if (!_rebuilding) _overlay = null; };
            _overlay.Show();
        }
        catch { /* overlay is optional; the wallpaper must still run */ }
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            if (_overlay != null)
            {
                try { _overlay.Close(); } catch { /* ignore */ }
                _overlay = null;
            }
            foreach (WallpaperForm form in _forms.ToArray())
                form.Close();
            _forms.Clear();
            Build();
        }
        finally
        {
            _rebuilding = false;
            _rebuildRequested = false;
            _pendingSignature = "";
        }
    }
}
