using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HtmlWallpaper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            MessageBox.Show(
                "HtmlWallpaper — render an HTML file or URL as your Windows desktop background.\n\n" +
                "Usage:\n" +
                "  HtmlWallpaper.exe <path-to-html | url> [--primary | --monitor N]\n\n" +
                "Options:\n" +
                "  (default)    Render on every monitor (one window per display).\n" +
                "  --primary    Cover only the primary monitor.\n" +
                "  --monitor N  Cover only monitor index N (0-based).\n\n" +
                "Stop it:\n" +
                "  End the HtmlWallpaper.exe process (Task Manager) or run refresh of the desktop.",
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
        Screen[] screens = Screen.AllScreens;

        int monitorFlag = Array.FindIndex(args, a =>
            string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase));
        if (monitorFlag >= 0 && monitorFlag + 1 < args.Length &&
            int.TryParse(args[monitorFlag + 1], out int idx) &&
            idx >= 0 && idx < screens.Length)
        {
            return new List<Rectangle> { screens[idx].Bounds };
        }

        if (args.Contains("--primary", StringComparer.OrdinalIgnoreCase))
        {
            return new List<Rectangle> { Screen.PrimaryScreen!.Bounds };
        }

        return screens.Select(s => s.Bounds).ToList();
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
