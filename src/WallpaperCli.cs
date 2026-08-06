using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HtmlWallpaper;

/// <summary>
/// Management command-line surface, folded into the host so there are no separate
/// PowerShell helper scripts:
/// <list type="bullet">
///   <item><c>HtmlWallpaper.exe set &lt;file|url&gt; [--primary | --monitor N]</c></item>
///   <item><c>HtmlWallpaper.exe stop</c></item>
///   <item><c>HtmlWallpaper.exe autostart on --source &lt;file|url&gt; [--primary]</c></item>
///   <item><c>HtmlWallpaper.exe autostart off</c></item>
/// </list>
/// Attaches to the launching console (the app is a WinExe) so messages are visible.
/// </summary>
internal static class WallpaperCli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private const string StartupLinkName = "HtmlWallpaper.lnk";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "set" or "stop" or "autostart";

    public static int Run(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        string verb = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return verb switch
        {
            "set" => CmdSet(rest),
            "stop" => CmdStop(),
            "autostart" => CmdAutostart(rest),
            _ => 2,
        };
    }

    // set <file|url> [--primary | --monitor N] — (re)launch the wallpaper.
    private static int CmdSet(string[] rest)
    {
        string? source = rest.FirstOrDefault(a => !a.StartsWith("--"));
        if (source is null)
        {
            Console.WriteLine("Usage: HtmlWallpaper.exe set <file|url> [--primary | --monitor N]");
            return 2;
        }
        source = ResolveSource(source);

        var psi = new ProcessStartInfo { FileName = ExePath, UseShellExecute = true };
        psi.ArgumentList.Add(source);
        foreach (string flag in DisplayFlags(rest)) psi.ArgumentList.Add(flag);
        // A fresh instance replaces any running one (single-instance handling in
        // Main). UseShellExecute launches it detached from this console so the
        // command returns immediately instead of blocking on the wallpaper.
        Process.Start(psi);
        Console.WriteLine($"Wallpaper set to: {source}");
        return 0;
    }

    // stop — end any running wallpaper and let the shell repaint the OS wallpaper.
    private static int CmdStop()
    {
        Process[] procs = Process.GetProcessesByName("HtmlWallpaper")
            .Where(p => p.Id != Environment.ProcessId).ToArray();
        if (procs.Length == 0)
        {
            Console.WriteLine("HtmlWallpaper is not running.");
            return 0;
        }
        foreach (Process p in procs)
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { /* ignore */ }
        }
        // Ask the shell to repaint the original wallpaper.
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll, UpdatePerUserSystemParameters")
            { UseShellExecute = false });
        }
        catch { /* best effort */ }
        Console.WriteLine("Wallpaper stopped.");
        return 0;
    }

    // autostart on|off — create or remove the per-user login shortcut.
    private static int CmdAutostart(string[] rest)
    {
        string mode = rest.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "";
        string lnkPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLinkName);

        switch (mode)
        {
            case "off":
                if (File.Exists(lnkPath))
                {
                    File.Delete(lnkPath);
                    Console.WriteLine("Startup entry removed.");
                }
                else Console.WriteLine("No startup entry found.");
                return 0;

            case "on":
                string? source = ArgValue(rest, "--source")
                    ?? rest.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                if (source is null)
                {
                    Console.WriteLine("Usage: HtmlWallpaper.exe autostart on --source <file|url> [--primary]");
                    return 2;
                }
                source = ResolveSource(source);
                string argLine = Quote(source);
                foreach (string flag in DisplayFlags(rest)) argLine += " " + flag;
                CreateShortcut(lnkPath, ExePath, argLine, Path.GetDirectoryName(ExePath)!);
                Console.WriteLine($"Startup entry created: {lnkPath}");
                Console.WriteLine("It will launch at your next login.");
                return 0;

            default:
                Console.WriteLine("Usage: HtmlWallpaper.exe autostart on --source <file|url> [--primary] | autostart off");
                return 2;
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static string ResolveSource(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? source
            : Path.GetFullPath(source);

    // Pass through the display-target flags the wallpaper understands.
    private static IEnumerable<string> DisplayFlags(string[] rest)
    {
        for (int i = 0; i < rest.Length; i++)
        {
            if (string.Equals(rest[i], "--primary", StringComparison.OrdinalIgnoreCase))
                yield return "--primary";
            else if (string.Equals(rest[i], "--monitor", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Length)
            {
                yield return "--monitor";
                yield return rest[i + 1];
            }
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring(name.Length + 1);
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    private static string Quote(string s) => "\"" + s + "\"";

    // Create a .lnk via the WScript.Shell COM object (same mechanism the old
    // Enable-Startup.ps1 used), avoiding an added COM interop reference.
    private static void CreateShortcut(string lnkPath, string target, string arguments, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            throw new InvalidOperationException("WScript.Shell is unavailable; cannot create the startup shortcut.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.Arguments = arguments;
            lnk.WorkingDirectory = workingDir;
            lnk.WindowStyle = 7; // minimized
            lnk.Description = "HTML live desktop wallpaper";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
