using System.Runtime.InteropServices;
using System.Windows.Forms;
using HtmlWallpaper.Modules;

namespace HtmlWallpaper;

/// <summary>
/// Command-line surface: <c>HtmlWallpaper.exe module &lt;list|enable|disable|refresh|add|registry&gt; [id|path]</c>.
/// Runs as a console-style operation even though the app is a WinExe, by attaching
/// to the launching console so output (and device-code prompts) are visible.
/// </summary>
internal static class ModuleCli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public static bool IsModuleCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "module", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        string[] sub = args.Skip(1).ToArray();
        string verb = sub.Length > 0 ? sub[0].ToLowerInvariant() : "help";
        string? id = sub.Length > 1 ? sub[1] : null;

        var service = new ModuleService();

        switch (verb)
        {
            case "list":
                return CmdList(service);

            case "registry":
                service.Registry.WriteRegistry();
                Console.WriteLine("Regenerated modules/registry.js");
                return 0;

            case "enable":
                if (id is null) { Console.WriteLine("Usage: module enable <id>"); return 2; }
                return UiRunner.Run(h => service.EnableAsync(id, interactive: true, h, Console.Out));

            case "disable":
                if (id is null) { Console.WriteLine("Usage: module disable <id>"); return 2; }
                return service.Disable(id, Console.Out) ? 0 : 1;

            case "refresh":
                if (id is null) { Console.WriteLine("Usage: module refresh <id>"); return 2; }
                return UiRunner.Run(h => service.RefreshAsync(id, interactive: true, h, Console.Out));

            case "add":
                if (id is null) { Console.WriteLine("Usage: module add <folder-path>"); return 2; }
                return CmdAdd(service, id);

            default:
                Console.WriteLine("HtmlWallpaper module commands:");
                Console.WriteLine("  module list                 List installed modules and their state");
                Console.WriteLine("  module enable <id>          Sign in / refresh, then turn a module on");
                Console.WriteLine("  module disable <id>         Turn a module off");
                Console.WriteLine("  module refresh <id>         Refresh a module's data now");
                Console.WriteLine("  module add <folder-path>    Install a module folder, then enable it");
                Console.WriteLine("  module registry             Rebuild modules/registry.js");
                return verb == "help" ? 0 : 2;
        }
    }

    private static int CmdList(ModuleService service)
    {
        List<ModuleManifest> mods = service.List();
        if (mods.Count == 0) { Console.WriteLine("No modules installed under modules/."); return 0; }
        Console.WriteLine($"{"ID",-14} {"ENABLED",-8} {"REFRESH",-8} NAME");
        foreach (ModuleManifest m in mods)
        {
            string enabled = service.IsEnabled(m.Id) ? "on" : "off";
            string refresh = m.Refresh is null ? "-" : $"{m.Refresh.EveryMinutes}m";
            Console.WriteLine($"{m.Id,-14} {enabled,-8} {refresh,-8} {m.Name}");
        }
        return 0;
    }

    private static int CmdAdd(ModuleService service, string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
        {
            Console.WriteLine($"Folder not found: {sourcePath}");
            return 1;
        }
        string manifest = Path.Combine(sourcePath, "module.json");
        ModuleManifest? m = ModuleManifest.Load(manifest);
        if (m is null) { Console.WriteLine($"No valid module.json in {sourcePath}"); return 1; }

        string dest = Path.Combine(ModulePaths.ModulesDir, m.Id);
        CopyDir(sourcePath, dest);
        service.Registry.WriteRegistry();
        Console.WriteLine($"Installed module '{m.Id}'. Enabling...");
        return UiRunner.Run(h => service.EnableAsync(m.Id, interactive: true, h, Console.Out));
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dest));
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dest), overwrite: true);
    }
}

/// <summary>
/// Runs an async operation that needs a live WinForms message pump and a parent
/// window handle (for the WAM sign-in dialog), returning a process exit code.
/// </summary>
internal static class UiRunner
{
    public static int Run(Func<IntPtr, Task<bool>> op)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int rc = 1;
        var ctx = new ApplicationContext();
        var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-4000, -4000),
            Size = new System.Drawing.Size(1, 1),
        };
        owner.CreateControl();
        IntPtr handle = owner.Handle;

        var sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);

        owner.BeginInvoke(new Action(async () =>
        {
            try { rc = await op(handle) ? 0 : 1; }
            catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); rc = 1; }
            finally { owner.Close(); ctx.ExitThread(); }
        }));

        Application.Run(ctx);
        owner.Dispose();
        return rc;
    }
}
