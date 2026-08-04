using System.Diagnostics;
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
                "  HtmlWallpaper.exe <path-to-html | url> [--primary]\n\n" +
                "Options:\n" +
                "  --primary   Cover only the primary monitor (default: all monitors).\n\n" +
                "Stop it:\n" +
                "  End the HtmlWallpaper.exe process (Task Manager) or run refresh of the desktop.",
                "HtmlWallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        string source = args[0];
        bool spanAll = !args.Contains("--primary", StringComparer.OrdinalIgnoreCase);

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
        Application.Run(new WallpaperForm(source, spanAll));
        return 0;
    }
}
