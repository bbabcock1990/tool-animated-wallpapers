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
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 1500 };
    // Safety net: even if a display event is missed (undock/lid-close/monitor
    // power transitions don't always raise DisplaySettingsChanged), poll the
    // monitor layout and rebuild when it no longer matches what we drew.
    private readonly System.Windows.Forms.Timer _reconcile = new() { Interval = 2000 };
    private bool _rebuilding;
    private int _generation;
    private string _builtSignature = "";

    // Module runtime (tray + in-process data scheduler), owned by the wallpaper
    // process so there is no separate tray process or scheduled task.
    private readonly Modules.ModuleService _moduleService = new();
    private Modules.ModuleScheduler? _scheduler;
    private Tray.ModuleTray? _tray;

    public MultiFormContext(string source, string[] args)
    {
        _source = source;
        _args = args;

        // Coalesce bursts of DisplaySettingsChanged events into a single rebuild.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Reconcile the drawn layout against the current monitors on a timer so a
        // missed display event can't leave a stale (wrong-size / off-screen) wallpaper.
        _reconcile.Tick += (_, _) => ReconcileLayout();
        _reconcile.Start();

        Build();

        // Start the module runtime once, after the UI thread/message loop exists.
        StartModuleRuntime();
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
            _scheduler?.Dispose();
            _tray?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Restart the debounce window; Windows fires several of these per change.
        _debounce.Stop();
        _debounce.Start();
    }

    private void ReconcileLayout()
    {
        if (_rebuilding) return;
        string current = Signature(Program.ResolveTargetScreens(_args));
        if (current != _builtSignature)
        {
            // Layout drifted from what we built (e.g. a missed event). Debounce a
            // rebuild so we settle after Windows finishes updating the display set.
            _debounce.Stop();
            _debounce.Start();
        }
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
        }
    }
}
