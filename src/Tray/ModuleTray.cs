using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using HtmlWallpaper.Modules;

namespace HtmlWallpaper.Tray;

/// <summary>
/// System-tray controller that lives inside the wallpaper host process. Shows a
/// checkable item per toggleable module (reflecting its enabled state), a
/// Settings dialog for the global toggle hotkey, and Quit. Replaces the old
/// separate PowerShell tray + VBScript launcher entirely.
/// </summary>
internal sealed class ModuleTray : IDisposable
{
    private readonly ModuleService _service;
    private readonly ModuleRegistry _registry;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<string, ToolStripMenuItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _settings;
    private readonly System.Windows.Forms.Timer _sync;
    private readonly Form _owner;

    // The two engine-level hotkeys. The tray persists them and raises HotkeysChanged;
    // the host (MultiFormContext) owns registration + the actions they trigger.
    private Hotkey _clickHotkey;
    private Hotkey _hideHotkey;

    /// <summary>Default clickable-mode hotkey (used when nothing is persisted).</summary>
    public static Hotkey DefaultClickHotkey => Hotkey.Parse("Ctrl+Alt+K");
    /// <summary>Default hide-all-widgets hotkey (used when nothing is persisted).</summary>
    public static Hotkey DefaultHideHotkey => Hotkey.Parse("Ctrl+Alt+H");

    public Hotkey ClickHotkey => _clickHotkey;
    public Hotkey HideHotkey => _hideHotkey;

    /// <summary>Raised after the user saves new hotkeys, so the host can re-register them.</summary>
    public event Action? HotkeysChanged;

    public ModuleTray(ModuleService service)
    {
        _service = service;
        _registry = service.Registry;

        // Hidden owner window: parents modal dialogs and the WAM sign-in prompt.
        _owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Size = new Size(1, 1),
        };
        _owner.CreateControl();
        _ = _owner.Handle; // force handle creation

        _menu = new ContextMenuStrip();

        List<ModuleManifest> toggleable = _registry.Discover().Where(m => m.Toggle).ToList();
        foreach (ModuleManifest m in toggleable)
        {
            var item = new ToolStripMenuItem(m.Name) { CheckOnClick = false, Tag = m.Id };
            string id = m.Id;
            item.Click += async (_, _) => await ToggleModuleAsync(id);
            _items[m.Id] = item;
            _menu.Items.Add(item);
        }
        if (toggleable.Count > 0)
            _menu.Items.Add(new ToolStripSeparator());

        // Link submenus: a module that publishes a tray links file gets a submenu of
        // clickable items that open in the browser. Populated lazily on open.
        List<ModuleManifest> linkMods = _registry.Discover()
            .Where(m => !string.IsNullOrWhiteSpace(m.Tray?.Links)).ToList();
        foreach (ModuleManifest m in linkMods)
        {
            string caption = string.IsNullOrWhiteSpace(m.Tray!.Title) ? m.Name : m.Tray!.Title!;
            var sub = new ToolStripMenuItem(caption);
            sub.DropDownItems.Add(new ToolStripMenuItem("(loading\u2026)") { Enabled = false });
            ModuleManifest captured = m;
            sub.DropDownOpening += (_, _) => PopulateLinks(sub, captured);
            _menu.Items.Add(sub);
        }
        if (linkMods.Count > 0)
            _menu.Items.Add(new ToolStripSeparator());

        _settings = new ToolStripMenuItem("Settings...");
        _settings.Click += (_, _) => ShowSettingsDialog();
        _menu.Items.Add(_settings);
        _menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit HtmlWallpaper");
        quit.Click += (_, _) => Application.Exit();
        _menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "HtmlWallpaper",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _menu.Show(Cursor.Position); };

        // Load persisted hotkeys (fall back to defaults).
        ModulesStateFile state = _registry.LoadState();
        _clickHotkey = string.IsNullOrWhiteSpace(state.Tray.ClickHotkey)
            ? DefaultClickHotkey : Hotkey.Parse(state.Tray.ClickHotkey);
        _hideHotkey = string.IsNullOrWhiteSpace(state.Tray.HideHotkey)
            ? DefaultHideHotkey : Hotkey.Parse(state.Tray.HideHotkey);

        _sync = new System.Windows.Forms.Timer { Interval = 2000 };
        _sync.Tick += (_, _) => SyncChecks();
        _sync.Start();

        SyncChecks();
    }

    private async Task ToggleModuleAsync(string id)
    {
        bool nowEnabled = _service.IsEnabled(id);
        if (nowEnabled)
        {
            _service.Disable(id, TextWriter.Null);
        }
        else
        {
            // Re-enable may need interactive sign-in (e.g. calendar) if no token is cached.
            await _service.EnableAsync(id, interactive: true, _owner.Handle, TextWriter.Null);
        }
        SyncChecks();
    }

    private void SyncChecks()
    {
        foreach (KeyValuePair<string, ToolStripMenuItem> kv in _items)
            kv.Value.Checked = _service.IsEnabled(kv.Key);
    }

    private void SaveTraySettings()
    {
        ModulesStateFile state = _registry.LoadState();
        state.Tray.ClickHotkey = _clickHotkey.ToString();
        state.Tray.HideHotkey = _hideHotkey.ToString();
        _registry.SaveState(state);
    }

    private void ShowSettingsDialog()
    {
        using var form = new Form
        {
            Text = "HtmlWallpaper Settings",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 250),
            TopMost = true,
        };

        var lblClick = new Label { Text = "Enable clicking (interact with panels) - click the box and press a combo:", AutoSize = true, Location = new Point(15, 18) };
        var tbClick = new TextBox { ReadOnly = true, Location = new Point(15, 42), Width = 390, TextAlign = HorizontalAlignment.Center, Text = _clickHotkey.ToString() };

        var lblHide = new Label { Text = "Hide / show all widgets - click the box and press a combo:", AutoSize = true, Location = new Point(15, 86) };
        var tbHide = new TextBox { ReadOnly = true, Location = new Point(15, 110), Width = 390, TextAlign = HorizontalAlignment.Center, Text = _hideHotkey.ToString() };

        var hint = new Label { Text = "Requires at least one of Ctrl / Alt / Shift plus a key.", AutoSize = true, ForeColor = Color.Gray, Location = new Point(15, 150) };

        Hotkey capturedClick = _clickHotkey;
        Hotkey capturedHide = _hideHotkey;
        CaptureInto(tbClick, hk => capturedClick = hk);
        CaptureInto(tbHide, hk => capturedHide = hk);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(235, 205) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(325, 205) };
        form.Controls.AddRange(new Control[] { lblClick, tbClick, lblHide, tbHide, hint, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(_owner) == DialogResult.OK)
        {
            if (!capturedClick.HasModifier || !capturedHide.HasModifier)
            {
                MessageBox.Show(_owner, "Each hotkey needs at least one modifier (Ctrl, Alt or Shift).",
                    "Invalid hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (capturedClick.ToString() == capturedHide.ToString())
            {
                MessageBox.Show(_owner, "The two hotkeys must be different.",
                    "Invalid hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _clickHotkey = capturedClick;
            _hideHotkey = capturedHide;
            SaveTraySettings();
            HotkeysChanged?.Invoke();
        }
    }

    /// <summary>Wire a read-only textbox to capture a pressed key-combo into a setter.</summary>
    private static void CaptureInto(TextBox tb, Action<Hotkey> set)
    {
        tb.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            Keys kc = e.KeyCode;
            if (kc is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            var hk = new Hotkey { Ctrl = e.Control, Alt = e.Alt, Shift = e.Shift, Win = false, Key = kc };
            set(hk);
            tb.Text = hk.ToString();
        };
    }

    /// <summary>
    /// Fill a module's links submenu from its generated JSON file (an array of
    /// { title, url, status } objects). Runs on every open so the newest refresh is
    /// reflected. Clicking an item opens its URL in the default browser.
    /// </summary>
    private static void PopulateLinks(ToolStripMenuItem parent, ModuleManifest m)
    {
        parent.DropDownItems.Clear();
        try
        {
            string path = Path.Combine(m.Dir, m.Tray!.Links!);
            if (!File.Exists(path))
            {
                parent.DropDownItems.Add(new ToolStripMenuItem("No updates yet") { Enabled = false });
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem("No updates yet") { Enabled = false });
                return;
            }

            int max = m.Tray!.Max > 0 ? m.Tray!.Max : 12;
            int shown = 0;
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (shown >= max) break;
                string url = el.TryGetProperty("url", out JsonElement u) ? (u.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                string title = el.TryGetProperty("title", out JsonElement t) ? (t.GetString() ?? "") : "";
                string status = el.TryGetProperty("status", out JsonElement s) ? (s.GetString() ?? "") : "";

                string label = string.IsNullOrWhiteSpace(status) ? title : $"[{status}]  {title}";
                if (label.Length > 90) label = label.Substring(0, 89) + "\u2026";

                string capturedUrl = url;
                var item = new ToolStripMenuItem(label);
                item.Click += (_, _) => OpenUrl(capturedUrl);
                parent.DropDownItems.Add(item);
                shown++;
            }

            if (parent.DropDownItems.Count == 0)
                parent.DropDownItems.Add(new ToolStripMenuItem("No updates yet") { Enabled = false });
        }
        catch
        {
            parent.DropDownItems.Clear();
            parent.DropDownItems.Add(new ToolStripMenuItem("Could not read updates") { Enabled = false });
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a browser is best-effort */ }
    }

    private static Icon LoadIcon()
    {
        // Prefer the embedded aurora icon, choosing the frame that matches the
        // system tray size for a crisp render.
        try
        {
            using Stream? s = typeof(ModuleTray).Assembly
                .GetManifestResourceStream("appicon.ico");
            if (s is not null)
                return new Icon(s, SystemInformation.SmallIconSize);
        }
        catch { /* fall through */ }
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null && File.Exists(exe))
            {
                Icon? ico = Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _sync.Stop();
        _sync.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _owner.Dispose();
    }
}
