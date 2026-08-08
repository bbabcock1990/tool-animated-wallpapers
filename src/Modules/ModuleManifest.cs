using System.Text.Json;
using System.Text.Json.Serialization;

namespace HtmlWallpaper.Modules;

/// <summary>
/// A wallpaper module is a self-contained folder under <c>modules/&lt;id&gt;</c>
/// described by a <c>module.json</c> manifest. The manifest declares the web
/// assets to inject into the wallpaper page, whether the module can be toggled
/// from the tray, and how (if at all) its data is refreshed.
/// </summary>
internal sealed class ModuleManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ModuleAssets Assets { get; set; } = new();

    /// <summary>Whether the module exposes an on/off toggle in the tray.</summary>
    public bool Toggle { get; set; } = true;

    /// <summary>Optional data-refresh descriptor (built-in handler or external command).</summary>
    public ModuleRefresh? Refresh { get; set; }

    /// <summary>Default global hotkey suggestion (e.g. "Ctrl+Alt+C").</summary>
    public string? HotkeyDefault { get; set; }

    /// <summary>Optional system-tray links submenu (e.g. recent Azure updates to open in a browser).</summary>
    public ModuleTrayLinks? Tray { get; set; }

    /// <summary>Free-form default settings surfaced to the module's JS.</summary>
    public JsonElement? Settings { get; set; }

    /// <summary>Absolute path of the module folder (populated at load, not from JSON).</summary>
    [JsonIgnore] public string Dir { get; set; } = "";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModuleManifest? Load(string manifestPath)
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            ModuleManifest? m = JsonSerializer.Deserialize<ModuleManifest>(json, ReadOptions);
            if (m is null || string.IsNullOrWhiteSpace(m.Id)) return null;
            m.Dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
            return m;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class ModuleAssets
{
    public List<string> Css { get; set; } = new();
    public List<string> Js { get; set; } = new();
}

internal sealed class ModuleRefresh
{
    public int EveryMinutes { get; set; } = 15;

    /// <summary>Name of a built-in C# refresher (e.g. "calendar").</summary>
    public string? Builtin { get; set; }

    /// <summary>External command line to run for data refresh (relative to the module dir).</summary>
    public string? Command { get; set; }
}

/// <summary>
/// Optional tray "links" submenu for a module. The refresher writes a JSON array
/// of <c>{ "title", "url", "status" }</c> objects to <see cref="Links"/> (relative
/// to the module folder); the tray renders them as clickable items that open the
/// URL in the default browser. This is how a display-only wallpaper panel (which
/// cannot receive mouse clicks) still lets the user open an item.
/// </summary>
internal sealed class ModuleTrayLinks
{
    /// <summary>Path (relative to the module dir) of the generated links JSON file.</summary>
    public string? Links { get; set; }

    /// <summary>Submenu caption. Falls back to the module name when omitted.</summary>
    public string? Title { get; set; }

    /// <summary>Maximum number of links to show. Defaults to 12.</summary>
    public int Max { get; set; } = 12;
}
