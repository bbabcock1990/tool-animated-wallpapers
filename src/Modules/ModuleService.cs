using System.Diagnostics;

namespace HtmlWallpaper.Modules;

/// <summary>
/// High-level operations on modules: list, enable, disable and refresh. Shared by
/// the command-line interface (Program.cs) and the tray. Enabling a module runs
/// its data refresh once (interactively, so first-time sign-in can happen); the
/// unattended <see cref="ModuleScheduler"/> keeps it fresh thereafter.
/// </summary>
internal sealed class ModuleService
{
    private readonly ModuleRegistry _registry = new();

    public ModuleRegistry Registry => _registry;

    public List<ModuleManifest> List() => _registry.Discover();

    public bool IsEnabled(string id) => _registry.IsEnabled(id);

    /// <summary>Enable a module: refresh its data once (if it has a refresher), then mark it on.</summary>
    public async Task<bool> EnableAsync(string id, bool interactive, IntPtr parentWindow, TextWriter log, string? authMethod = null)
    {
        ModuleManifest? m = _registry.Find(id);
        if (m is null) { log.WriteLine($"Module '{id}' not found under modules/."); return false; }

        // Persist a chosen sign-in method (calendar) before the first refresh so
        // both the interactive enable and the unattended scheduler use it.
        if (!string.IsNullOrWhiteSpace(authMethod) &&
            string.Equals(m.Refresh?.Builtin, "calendar", StringComparison.OrdinalIgnoreCase))
        {
            try { CalendarRefresher.SaveAuthMethod(m, authMethod!); }
            catch (Exception ex) { log.WriteLine($"Calendar: could not save auth method: {ex.Message}"); }
        }

        if (m.Refresh is not null)
        {
            bool ok = await RefreshOnceAsync(m, interactive, parentWindow, log);
            if (!ok)
            {
                log.WriteLine($"Module '{id}' not enabled: initial data refresh failed.");
                return false;
            }
        }

        _registry.SetEnabled(id, true);
        log.WriteLine($"Module '{m.Name}' enabled.");
        return true;
    }

    public bool Disable(string id, TextWriter log)
    {
        ModuleManifest? m = _registry.Find(id);
        if (m is null) { log.WriteLine($"Module '{id}' not found."); return false; }
        _registry.SetEnabled(id, false);
        log.WriteLine($"Module '{m.Name}' disabled.");
        return true;
    }

    /// <summary>Refresh a module's data now. Returns false if the module has no refresher or it failed.</summary>
    public async Task<bool> RefreshAsync(string id, bool interactive, IntPtr parentWindow, TextWriter log)
    {
        ModuleManifest? m = _registry.Find(id);
        if (m is null) { log.WriteLine($"Module '{id}' not found."); return false; }
        if (m.Refresh is null) { log.WriteLine($"Module '{id}' has no refresh step."); return true; }
        return await RefreshOnceAsync(m, interactive, parentWindow, log);
    }

    internal async Task<bool> RefreshOnceAsync(ModuleManifest m, bool interactive, IntPtr parentWindow, TextWriter log)
    {
        // Built-in refreshers implemented in C#.
        if (string.Equals(m.Refresh?.Builtin, "calendar", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var refresher = new CalendarRefresher(m);
                return await refresher.RefreshAsync(interactive, parentWindow, log);
            }
            catch (Exception ex)
            {
                log.WriteLine($"Module '{m.Id}' refresh error: {ex.Message}");
                return false;
            }
        }

        // External command refreshers (any language) run in the module folder.
        if (!string.IsNullOrWhiteSpace(m.Refresh?.Command))
            return RunCommand(m, log);

        return true;
    }

    private static bool RunCommand(ModuleManifest m, TextWriter log)
    {
        try
        {
            string cmd = m.Refresh!.Command!;
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = m.Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Allow either a bare .ps1 file name or a full command line.
            if (cmd.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(m.Dir, cmd)}\"";
            else
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"";

            using Process? p = Process.Start(psi);
            if (p is null) return false;
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!string.IsNullOrWhiteSpace(outp)) log.WriteLine(outp.TrimEnd());
            if (p.ExitCode != 0)
            {
                log.WriteLine($"Module '{m.Id}' command exited {p.ExitCode}. {err.TrimEnd()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.WriteLine($"Module '{m.Id}' command error: {ex.Message}");
            return false;
        }
    }
}
