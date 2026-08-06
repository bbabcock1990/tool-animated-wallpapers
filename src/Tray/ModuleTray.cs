using System.Drawing;
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
    private readonly GlobalHotkeyWindow _hk;
    private readonly Form _owner;

    private Hotkey _hotkey = Hotkey.Default;
    private string? _hotkeyTarget;

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

        // Load persisted hotkey + target.
        ModulesStateFile state = _registry.LoadState();
        _hotkey = Hotkey.Parse(state.Tray.Hotkey ?? toggleable.FirstOrDefault()?.HotkeyDefault);
        _hotkeyTarget = state.Tray.HotkeyTarget ?? toggleable.FirstOrDefault()?.Id;

        _hk = new GlobalHotkeyWindow();
        _hk.HotkeyPressed += async (_, _) => await ToggleHotkeyTargetAsync();
        ApplyHotkey();

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

    private async Task ToggleHotkeyTargetAsync()
    {
        if (string.IsNullOrWhiteSpace(_hotkeyTarget)) return;
        await ToggleModuleAsync(_hotkeyTarget);
    }

    private void SyncChecks()
    {
        foreach (KeyValuePair<string, ToolStripMenuItem> kv in _items)
            kv.Value.Checked = _service.IsEnabled(kv.Key);
        _settings.Text = $"Settings (hotkey: {_hotkey})...";
    }

    private void ApplyHotkey()
    {
        bool ok = _hk.Register(_hotkey);
        if (!ok)
        {
            _icon.ShowBalloonTip(4000, "Hotkey unavailable",
                $"Could not register {_hotkey}. Another app may use it. Pick another combo in Settings.",
                ToolTipIcon.Warning);
        }
    }

    private void SaveTraySettings()
    {
        ModulesStateFile state = _registry.LoadState();
        state.Tray.Hotkey = _hotkey.ToString();
        state.Tray.HotkeyTarget = _hotkeyTarget;
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
            ClientSize = new Size(400, 230),
            TopMost = true,
        };

        var lblTarget = new Label { Text = "Hotkey toggles module:", AutoSize = true, Location = new Point(15, 18) };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(15, 42), Width = 370 };
        List<ModuleManifest> toggleable = _registry.Discover().Where(m => m.Toggle).ToList();
        foreach (ModuleManifest m in toggleable) combo.Items.Add(m.Id);
        combo.SelectedItem = _hotkeyTarget is not null && combo.Items.Contains(_hotkeyTarget) ? _hotkeyTarget : (combo.Items.Count > 0 ? combo.Items[0] : null);

        var lblHk = new Label { Text = "Toggle hotkey - click the box and press a combo (e.g. Ctrl+Alt+C):", AutoSize = true, Location = new Point(15, 82) };
        var tb = new TextBox { ReadOnly = true, Location = new Point(15, 106), Width = 370, TextAlign = HorizontalAlignment.Center, Text = _hotkey.ToString() };
        var hint = new Label { Text = "Requires at least one of Ctrl / Alt / Shift plus a key.", AutoSize = true, ForeColor = Color.Gray, Location = new Point(15, 136) };

        Hotkey captured = _hotkey;
        tb.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            Keys kc = e.KeyCode;
            if (kc is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            captured = new Hotkey { Ctrl = e.Control, Alt = e.Alt, Shift = e.Shift, Win = false, Key = kc };
            tb.Text = captured.ToString();
        };

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(215, 185) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(305, 185) };
        form.Controls.AddRange(new Control[] { lblTarget, combo, lblHk, tb, hint, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(_owner) == DialogResult.OK)
        {
            if (!captured.HasModifier)
            {
                MessageBox.Show(_owner, "Please include at least one modifier (Ctrl, Alt or Shift).",
                    "Invalid hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _hotkey = captured;
            _hotkeyTarget = combo.SelectedItem as string;
            SaveTraySettings();
            ApplyHotkey();
            SyncChecks();
        }
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
        _hk.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _owner.Dispose();
    }
}
