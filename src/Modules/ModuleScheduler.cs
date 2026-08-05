using System.Collections.Concurrent;

namespace HtmlWallpaper.Modules;

/// <summary>
/// In-process replacement for the old per-module Scheduled Task. Because the
/// wallpaper host runs continuously while the user is signed in, it can refresh
/// each enabled module's data on its own cadence directly — no schtasks, no
/// separate PowerShell process. Refreshes are unattended (silent token only); a
/// module that needs interactive sign-in is handled by <c>module enable</c>.
/// </summary>
internal sealed class ModuleScheduler : IDisposable
{
    private readonly ModuleService _service;
    private readonly TextWriter _log;
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _reconcile;
    private bool _disposed;

    public ModuleScheduler(ModuleService service, TextWriter? log = null)
    {
        _service = service;
        _log = log ?? TextWriter.Null;
        // Re-evaluate which modules should be running every 60s so enable/disable
        // done at runtime (CLI or tray) is picked up without a restart.
        _reconcile = new System.Threading.Timer(_ => Reconcile(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    private void Reconcile()
    {
        if (_disposed) return;
        try
        {
            var shouldRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModuleManifest m in _service.List())
            {
                if (m.Refresh is null) continue;
                if (!_service.IsEnabled(m.Id)) continue;
                shouldRun.Add(m.Id);
                if (!_timers.ContainsKey(m.Id))
                {
                    int minutes = Math.Max(1, m.Refresh.EveryMinutes);
                    var period = TimeSpan.FromMinutes(minutes);
                    // Fire almost immediately once, then every N minutes.
                    var t = new System.Threading.Timer(_ => Tick(m.Id), null, TimeSpan.FromSeconds(3), period);
                    _timers[m.Id] = t;
                }
            }
            // Stop timers for modules that are no longer enabled/refreshable.
            foreach (string id in _timers.Keys.ToArray())
            {
                if (!shouldRun.Contains(id) && _timers.TryRemove(id, out System.Threading.Timer? t))
                    t.Dispose();
            }
        }
        catch { /* never let the scheduler throw on a background thread */ }
    }

    private void Tick(string id)
    {
        if (_disposed) return;
        if (!_service.IsEnabled(id)) return;
        try
        {
            // Unattended: silent token only. No parent window.
            _service.RefreshAsync(id, interactive: false, parentWindow: IntPtr.Zero, _log)
                    .GetAwaiter().GetResult();
        }
        catch { /* transient refresh failures are non-fatal */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _reconcile.Dispose();
        foreach (System.Threading.Timer t in _timers.Values) t.Dispose();
        _timers.Clear();
    }
}
