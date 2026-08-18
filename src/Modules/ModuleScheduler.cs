using System.Collections.Concurrent;

namespace HtmlWallpaper.Modules;

/// <summary>
/// In-process replacement for the old per-module Scheduled Task. Because the
/// wallpaper host runs continuously while the user is signed in, it can refresh
/// each enabled module's data on its own cadence directly — no schtasks, no
/// separate PowerShell process. Refreshes are unattended (silent token only); a
/// module that needs interactive sign-in is handled by <c>module enable</c>.
///
/// Scheduling is driven by a single short "heartbeat" that decides which modules
/// are due from wall-clock timestamps, rather than one long-period timer per
/// module. Long-period <see cref="System.Threading.Timer"/>s were observed to
/// stop firing across extended Modern Standby / sleep and never resume, which
/// silently froze every module's data (e.g. the panel stuck on the previous
/// afternoon's updates after an overnight standby). A short self-rescheduling
/// heartbeat recovers on its own, catches up any missed cadence immediately when
/// it next runs, and is additionally kicked on power-resume.
/// </summary>
internal sealed class ModuleScheduler : IDisposable
{
    private readonly ModuleService _service;
    private readonly TextWriter _log;

    // Wall-clock time of each module's last refresh start, so the heartbeat can
    // tell what is due even after the process was suspended for hours.
    private readonly ConcurrentDictionary<string, DateTime> _lastRunUtc = new(StringComparer.OrdinalIgnoreCase);
    // Modules currently being refreshed, so a slow refresh isn't started twice.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // One self-rescheduling one-shot timer. A one-shot that re-arms itself in a
    // finally block (rather than a periodic timer) guarantees forward progress:
    // a single slow/stalled beat can't leave the loop permanently un-armed.
    private readonly System.Threading.Timer _heartbeat;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private volatile bool _disposed;

    public ModuleScheduler(ModuleService service, TextWriter? log = null)
    {
        _service = service;
        _log = log ?? TextWriter.Null;

        // ThreadPool timers do not reliably resume firing after long Modern
        // Standby / sleep; re-kick the heartbeat the moment the machine wakes so
        // stale module data is refreshed promptly instead of on the next full cycle.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _heartbeat = new System.Threading.Timer(_ => Beat(), null,
            TimeSpan.FromSeconds(3), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            // Fire a catch-up beat shortly after resume (give the network stack a
            // moment to come back). Change() re-arms even a timer that stopped
            // firing while suspended.
            try { _heartbeat.Change(TimeSpan.FromSeconds(2), System.Threading.Timeout.InfiniteTimeSpan); }
            catch { /* disposed */ }
        }
    }

    private void Beat()
    {
        if (_disposed) return;
        try
        {
            DateTime now = DateTime.UtcNow;
            foreach (ModuleManifest m in _service.List())
            {
                if (m.Refresh is null) continue;
                if (!_service.IsEnabled(m.Id)) continue;

                int minutes = Math.Max(1, m.Refresh.EveryMinutes);
                DateTime last = _lastRunUtc.TryGetValue(m.Id, out DateTime t) ? t : DateTime.MinValue;
                if (now - last >= TimeSpan.FromMinutes(minutes))
                    StartRefresh(m.Id, now);
            }
        }
        catch { /* never let the scheduler throw on a background thread */ }
        finally
        {
            // Re-arm for the next beat regardless of what happened above.
            if (!_disposed)
            {
                try { _heartbeat.Change(HeartbeatInterval, System.Threading.Timeout.InfiniteTimeSpan); }
                catch { /* disposed */ }
            }
        }
    }

    private void StartRefresh(string id, DateTime now)
    {
        // Skip if a previous refresh for this module is still running (a hung
        // command must not pile up or block the heartbeat).
        if (!_inFlight.TryAdd(id, 0)) return;

        // Record the start time up front so a slow refresh doesn't re-trigger on
        // the next beat, and so cadence is measured from start (not completion).
        _lastRunUtc[id] = now;

        // Offload the actual (blocking) refresh so one slow module can neither
        // stall the heartbeat nor delay other modules.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (_disposed || !_service.IsEnabled(id)) return;
                _service.RefreshAsync(id, interactive: false, parentWindow: IntPtr.Zero, _log)
                        .GetAwaiter().GetResult();
            }
            catch { /* transient refresh failures are non-fatal */ }
            finally { _inFlight.TryRemove(id, out _); }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        _heartbeat.Dispose();
    }
}
