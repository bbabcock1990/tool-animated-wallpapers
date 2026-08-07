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

        // Decide which monitors to cover, and keep them correct as displays change.
        Application.Run(new MultiFormContext(source, args));
        return 0;
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

        // Poll the live monitor layout every second on a thread-pool timer and rebuild
        // when it drifts from what we drew. Independent of the UI message pump.
        _poll = new System.Threading.Timer(_ => Poll(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
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
