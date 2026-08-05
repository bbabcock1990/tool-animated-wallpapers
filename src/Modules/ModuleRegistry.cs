using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HtmlWallpaper.Modules;

/// <summary>
/// Resolves the content root (the folder that holds <c>wallpaper.html</c> and the
/// <c>modules/</c> directory). Works both in the installed flat layout (exe next
/// to wallpaper.html) and in a dev build tree (exe under bin\Release\...).
/// </summary>
internal static class ModulePaths
{
    private static string? _contentRoot;

    public static string ContentRoot => _contentRoot ??= FindContentRoot();
    public static string ModulesDir => Path.Combine(ContentRoot, "modules");
    public static string StateFile => Path.Combine(ModulesDir, "state.json");
    public static string RegistryJs => Path.Combine(ModulesDir, "registry.js");

    private static string FindContentRoot()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var candidates = new List<string>();
        for (DirectoryInfo? d = new(exeDir); d is not null; d = d.Parent)
            candidates.Add(d.FullName);
        candidates.Add(Directory.GetCurrentDirectory());
        foreach (string c in candidates)
            if (File.Exists(Path.Combine(c, "wallpaper.html")))
                return c;
        return exeDir;
    }
}

internal sealed class ModuleState
{
    public bool Enabled { get; set; }
}

internal sealed class TraySettings
{
    public string? Hotkey { get; set; }
    public string? HotkeyTarget { get; set; }
}

internal sealed class ModulesStateFile
{
    public Dictionary<string, ModuleState> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TraySettings Tray { get; set; } = new();
}

/// <summary>
/// Discovers installed modules, tracks their enabled/disabled state in
/// <c>modules/state.json</c>, and compiles the browser-readable
/// <c>modules/registry.js</c> that <c>module-loader.js</c> consumes.
/// </summary>
internal sealed class ModuleRegistry
{
    private static readonly JsonSerializerOptions StateOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<ModuleManifest> Discover()
    {
        var list = new List<ModuleManifest>();
        if (!Directory.Exists(ModulePaths.ModulesDir)) return list;
        foreach (string dir in Directory.GetDirectories(ModulePaths.ModulesDir))
        {
            string manifest = Path.Combine(dir, "module.json");
            if (!File.Exists(manifest)) continue;
            ModuleManifest? m = ModuleManifest.Load(manifest);
            if (m is not null) list.Add(m);
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ModuleManifest? Find(string id) =>
        Discover().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public ModulesStateFile LoadState()
    {
        try
        {
            if (File.Exists(ModulePaths.StateFile))
            {
                string json = File.ReadAllText(ModulePaths.StateFile);
                ModulesStateFile? s = JsonSerializer.Deserialize<ModulesStateFile>(json, StateOpts);
                if (s is not null) return s;
            }
        }
        catch { /* fall through to default */ }
        return new ModulesStateFile();
    }

    public void SaveState(ModulesStateFile state)
    {
        Directory.CreateDirectory(ModulePaths.ModulesDir);
        string json = JsonSerializer.Serialize(state, StateOpts);
        File.WriteAllText(ModulePaths.StateFile, json, new UTF8Encoding(false));
    }

    public bool IsEnabled(string id)
    {
        ModulesStateFile s = LoadState();
        return s.Modules.TryGetValue(id, out ModuleState? st) && st.Enabled;
    }

    public void SetEnabled(string id, bool enabled)
    {
        ModulesStateFile s = LoadState();
        if (!s.Modules.TryGetValue(id, out ModuleState? st)) { st = new ModuleState(); s.Modules[id] = st; }
        st.Enabled = enabled;
        SaveState(s);
        WriteRegistry();
    }

    /// <summary>Compile modules/registry.js from the discovered modules + saved state.</summary>
    public void WriteRegistry()
    {
        List<ModuleManifest> mods = Discover();
        ModulesStateFile state = LoadState();

        var entries = new List<Dictionary<string, object?>>();
        foreach (ModuleManifest m in mods)
        {
            bool enabled = state.Modules.TryGetValue(m.Id, out ModuleState? st) && st.Enabled;
            entries.Add(new Dictionary<string, object?>
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["enabled"] = enabled,
                ["toggle"] = m.Toggle,
                ["css"] = m.Assets.Css.Select(a => $"modules/{m.Id}/{a}").ToList(),
                ["js"] = m.Assets.Js.Select(a => $"modules/{m.Id}/{a}").ToList(),
                ["settings"] = m.Settings.HasValue ? (object)m.Settings.Value : new Dictionary<string, object?>(),
            });
        }

        var doc = new Dictionary<string, object?>
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("o"),
            ["modules"] = entries,
        };

        var opts = new JsonSerializerOptions { WriteIndented = true };
        string js = "/* Generated by HtmlWallpaper module runtime. Do not edit by hand. */\n" +
                    "window.WALLPAPER_REGISTRY = " + JsonSerializer.Serialize(doc, opts) + ";\n";

        Directory.CreateDirectory(ModulePaths.ModulesDir);
        File.WriteAllText(ModulePaths.RegistryJs, js, new UTF8Encoding(false));
    }

    /// <summary>The module a single global hotkey should toggle (settings override, else first toggleable).</summary>
    public string? ToggleHotkeyTarget()
    {
        ModulesStateFile state = LoadState();
        if (!string.IsNullOrWhiteSpace(state.Tray.HotkeyTarget)) return state.Tray.HotkeyTarget;
        return Discover().FirstOrDefault(m => m.Toggle)?.Id;
    }
}
