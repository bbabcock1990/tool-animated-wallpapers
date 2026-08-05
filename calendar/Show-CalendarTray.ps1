<#
.SYNOPSIS
  System-tray control to turn the wallpaper calendar overlay on/off, with a
  configurable global hotkey.

.DESCRIPTION
  Adds a notification-area icon with a checkable "Calendar" item, a "Settings..."
  dialog, and "Exit". Toggling writes calendar\overlay-state.js (via
  Toggle-Calendar.ps1), which calendar-overlay.js polls, so the panel
  appears/disappears on every monitor within ~2s. The check mark stays in sync
  even if the state is changed elsewhere (e.g. the CLI).

  A global hotkey (default Ctrl+Alt+C) flips the overlay from anywhere. The
  combo is stored in calendar\settings.json and can be changed at runtime from
  the Settings dialog. Only one tray instance runs at a time.

.EXAMPLE
  .\Show-CalendarTray.ps1              # run the tray icon (foreground / hidden window)
  .\Show-CalendarTray.ps1 -Install     # also run automatically at login
  .\Show-CalendarTray.ps1 -Uninstall   # remove the login entry
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$NoStart
)

$here         = $PSScriptRoot
$toggle       = Join-Path $here 'Toggle-Calendar.ps1'
$stateFile    = Join-Path $here 'overlay-state.js'
$settingsFile = Join-Path $here 'settings.json'
$vbsPath      = Join-Path $here 'Start-CalendarTray.vbs'
$lnkName      = 'HtmlWallpaper-CalendarTray.lnk'
$startup      = [Environment]::GetFolderPath('Startup')
$lnkPath      = Join-Path $startup $lnkName

if ($Uninstall) {
    if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force }
    Write-Host "Removed tray startup entry."
    return
}

if ($Install) {
    # Launch via wscript.exe + a VBS shim so PowerShell starts fully hidden with
    # no console window (a hidden -WindowStyle console can still flash/persist and,
    # if closed, would kill the tray).
    $wscript = Join-Path $env:WINDIR 'System32\wscript.exe'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $wscript
    $lnk.Arguments  = "`"$vbsPath`""
    $lnk.WorkingDirectory = $here
    $lnk.WindowStyle = 7
    $lnk.Description = "HtmlWallpaper calendar overlay tray toggle"
    $lnk.Save()
    Write-Host "Tray will start at login (no console window): $lnkPath"
    # -NoStart just registers the login entry and returns, WITHOUT falling through
    # to the tray message loop (Application.Run) below. Installers use this so they
    # never block; they start the tray separately via Start-CalendarTray.vbs.
    if ($NoStart) { return }
    # Fall through and also start it now.
}

# --- Single-instance guard ---
$mutex = New-Object System.Threading.Mutex($false, 'Global\HtmlWallpaperCalendarTray')
if (-not $mutex.WaitOne(0)) {
    Write-Host "Tray already running."
    return
}

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

# Hidden message-only window that owns a global hotkey and raises an event
# when it is pressed. Registering the hotkey against this window means WM_HOTKEY
# is delivered to our WndProc on the tray's message-loop thread.
$hotkeyCs = @'
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class GlobalHotkeyWindow : NativeWindow, IDisposable {
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4B1D;
    public event EventHandler HotkeyPressed;
    public GlobalHotkeyWindow() {
        CreateParams cp = new CreateParams();
        cp.Parent = (IntPtr)(-3); // HWND_MESSAGE
        this.CreateHandle(cp);
    }
    public bool Register(uint modifiers, uint vk) {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        return RegisterHotKey(this.Handle, HOTKEY_ID, modifiers, vk);
    }
    public void Unregister() {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_HOTKEY) {
            EventHandler h = HotkeyPressed;
            if (h != null) h(this, EventArgs.Empty);
        }
        base.WndProc(ref m);
    }
    public void Dispose() {
        Unregister();
        this.DestroyHandle();
    }
}
'@
# Reference the actual loaded assemblies by path so this compiles on both
# Windows PowerShell 5.1 (.NET Framework, where Message lives in
# System.Windows.Forms) and PowerShell 7+ (.NET, where Message has been
# forwarded to System.Windows.Forms.Primitives). Referencing only
# "System.Windows.Forms" by name fails with CS1069 on .NET.
$hotkeyRefs = New-Object System.Collections.Generic.List[string]
$hotkeyRefs.Add([System.Windows.Forms.Form].Assembly.Location)
$hotkeyRefs.Add([System.Windows.Forms.NativeWindow].Assembly.Location)
$hotkeyRefs.Add([System.Windows.Forms.Message].Assembly.Location)
$hotkeyRefs.Add([System.ComponentModel.Component].Assembly.Location)
$hotkeyRefs = @($hotkeyRefs | Where-Object { $_ } | Sort-Object -Unique)
Add-Type -ReferencedAssemblies $hotkeyRefs -TypeDefinition $hotkeyCs -Language CSharp

# --- State (overlay on/off) ---
function Get-State {
    if (Test-Path $stateFile) {
        $txt = Get-Content $stateFile -Raw
        if ($txt -match '"calendar"\s*:\s*(true|false)') { return ($matches[1] -eq 'true') }
    }
    return $true
}

# --- Settings (hotkey) ---
function Get-DefaultHotkey {
    return [ordered]@{ ctrl = $true; alt = $true; shift = $false; win = $false; vk = 67; label = 'C' }
}

function Load-Settings {
    if (Test-Path $settingsFile) {
        try {
            $j = Get-Content $settingsFile -Raw | ConvertFrom-Json
            if ($j.hotkey) {
                return [ordered]@{
                    ctrl  = [bool]$j.hotkey.ctrl
                    alt   = [bool]$j.hotkey.alt
                    shift = [bool]$j.hotkey.shift
                    win   = [bool]$j.hotkey.win
                    vk    = [int]$j.hotkey.vk
                    label = [string]$j.hotkey.label
                }
            }
        } catch {}
    }
    return Get-DefaultHotkey
}

function Save-Settings($h) {
    $obj = @{ hotkey = @{ ctrl = $h.ctrl; alt = $h.alt; shift = $h.shift; win = $h.win; vk = $h.vk; label = $h.label } }
    $json = $obj | ConvertTo-Json -Depth 5
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($settingsFile, $json, $enc)
}

function Get-ModFlags($h) {
    $MOD_ALT = 1; $MOD_CONTROL = 2; $MOD_SHIFT = 4; $MOD_WIN = 8; $MOD_NOREPEAT = 0x4000
    $m = $MOD_NOREPEAT
    if ($h.alt)   { $m = $m -bor $MOD_ALT }
    if ($h.ctrl)  { $m = $m -bor $MOD_CONTROL }
    if ($h.shift) { $m = $m -bor $MOD_SHIFT }
    if ($h.win)   { $m = $m -bor $MOD_WIN }
    return [uint32]$m
}

function Format-KeyLabel($label) {
    if ($label -match '^D(\d)$')      { return $matches[1] }
    if ($label -match '^NumPad(\d)$') { return "Num$($matches[1])" }
    return $label
}

function Format-Hotkey($h) {
    $parts = @()
    if ($h.ctrl)  { $parts += 'Ctrl' }
    if ($h.alt)   { $parts += 'Alt' }
    if ($h.shift) { $parts += 'Shift' }
    if ($h.win)   { $parts += 'Win' }
    $parts += (Format-KeyLabel $h.label)
    return ($parts -join '+')
}

# Try a themed icon from the built exe; fall back to a stock icon.
function Get-TrayIcon {
    try {
        $exe = Join-Path (Split-Path $here -Parent) 'bin\Release\net8.0-windows\HtmlWallpaper.exe'
        if (Test-Path $exe) { return [System.Drawing.Icon]::ExtractAssociatedIcon($exe) }
    } catch {}
    return [System.Drawing.SystemIcons]::Application
}

# --- Tray UI ---
$menu    = New-Object System.Windows.Forms.ContextMenuStrip
$miCal   = New-Object System.Windows.Forms.ToolStripMenuItem 'Calendar'
$miCal.CheckOnClick = $false
$miSet   = New-Object System.Windows.Forms.ToolStripMenuItem 'Settings...'
$miExit  = New-Object System.Windows.Forms.ToolStripMenuItem 'Exit'
[void]$menu.Items.Add($miCal)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($miSet)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($miExit)

$icon = New-Object System.Windows.Forms.NotifyIcon
$icon.Icon = Get-TrayIcon
$icon.Text = 'Wallpaper calendar'
$icon.Visible = $true
$icon.ContextMenuStrip = $menu

# Current hotkey settings + the global hotkey window.
$script:hotkey = Load-Settings
$hk = New-Object GlobalHotkeyWindow

function Sync-Check {
    $on = Get-State
    $miCal.Checked = $on
    $miSet.Text = "Settings (hotkey: $(Format-Hotkey $script:hotkey))..."
    $icon.Text  = "Wallpaper calendar: $(if ($on) { 'On' } else { 'Off' })"
}

function Do-Toggle {
    $desired = -not (Get-State)
    if ($desired) { & $toggle -On | Out-Null } else { & $toggle -Off | Out-Null }
    Sync-Check
}

function Apply-Hotkey {
    $ok = $hk.Register((Get-ModFlags $script:hotkey), [uint32]$script:hotkey.vk)
    if (-not $ok) {
        $icon.ShowBalloonTip(4000, 'Hotkey unavailable',
            "Could not register $(Format-Hotkey $script:hotkey). Another app may already use it. Pick a different combo in Settings.",
            [System.Windows.Forms.ToolTipIcon]::Warning)
    }
    return $ok
}

function Show-SettingsDialog {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Calendar Overlay Settings'
    $form.FormBorderStyle = 'FixedDialog'
    $form.StartPosition = 'CenterScreen'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(380, 170)
    $form.TopMost = $true

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = 'Toggle hotkey - click the box and press the combo (e.g. Ctrl+Alt+C):'
    $lbl.AutoSize = $true
    $lbl.Location = New-Object System.Drawing.Point(15, 15)
    $form.Controls.Add($lbl)

    $tb = New-Object System.Windows.Forms.TextBox
    $tb.ReadOnly = $true
    $tb.Location = New-Object System.Drawing.Point(15, 45)
    $tb.Width = 350
    $tb.TextAlign = 'Center'
    $tb.Text = Format-Hotkey $script:hotkey
    $form.Controls.Add($tb)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = 'Requires at least one of Ctrl / Alt / Shift plus a key.'
    $hint.AutoSize = $true
    $hint.ForeColor = [System.Drawing.Color]::Gray
    $hint.Location = New-Object System.Drawing.Point(15, 75)
    $form.Controls.Add($hint)

    # Capture buffer (script scope so the event handler can see it).
    $script:cap = [ordered]@{
        ctrl = $script:hotkey.ctrl; alt = $script:hotkey.alt; shift = $script:hotkey.shift
        win = $script:hotkey.win; vk = $script:hotkey.vk; label = $script:hotkey.label
    }

    $tb.Add_KeyDown({
        param($s, $e)
        $e.SuppressKeyPress = $true
        $kc = $e.KeyCode
        # Ignore a modifier pressed on its own.
        if ($kc -eq [System.Windows.Forms.Keys]::ControlKey -or
            $kc -eq [System.Windows.Forms.Keys]::ShiftKey   -or
            $kc -eq [System.Windows.Forms.Keys]::Menu       -or
            $kc -eq [System.Windows.Forms.Keys]::LWin       -or
            $kc -eq [System.Windows.Forms.Keys]::RWin) { return }
        $script:cap.ctrl  = [bool]$e.Control
        $script:cap.alt   = [bool]$e.Alt
        $script:cap.shift = [bool]$e.Shift
        $script:cap.win   = $false
        $script:cap.vk    = [int]$kc
        $script:cap.label = $kc.ToString()
        $s.Text = Format-Hotkey $script:cap
    }) | Out-Null

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = 'Save'
    $btnOk.Location = New-Object System.Drawing.Point(200, 125)
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($btnOk)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = 'Cancel'
    $btnCancel.Location = New-Object System.Drawing.Point(290, 125)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($btnCancel)

    $form.AcceptButton = $btnOk
    $form.CancelButton = $btnCancel

    $result = $form.ShowDialog()
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        # Require at least one modifier so the hotkey doesn't hijack a bare key.
        if (-not ($script:cap.ctrl -or $script:cap.alt -or $script:cap.shift -or $script:cap.win)) {
            [System.Windows.Forms.MessageBox]::Show(
                'Please include at least one modifier (Ctrl, Alt or Shift).',
                'Invalid hotkey', 'OK', 'Warning') | Out-Null
        } else {
            $script:hotkey = [ordered]@{
                ctrl = $script:cap.ctrl; alt = $script:cap.alt; shift = $script:cap.shift
                win = $script:cap.win; vk = $script:cap.vk; label = $script:cap.label
            }
            Save-Settings $script:hotkey
            Apply-Hotkey | Out-Null
            Sync-Check
        }
    }
    $form.Dispose()
}

# --- Wire events ---
$miCal.Add_Click({ Do-Toggle }) | Out-Null
$miSet.Add_Click({ Show-SettingsDialog }) | Out-Null

# Left-click the icon also toggles.
$icon.Add_MouseClick({
    param($s, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Do-Toggle }
}) | Out-Null

# Global hotkey flips the overlay.
$hk.add_HotkeyPressed({ Do-Toggle }) | Out-Null

# Keep the check mark in sync if state is changed elsewhere (e.g. the CLI).
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({ Sync-Check }) | Out-Null
$timer.Start()

$miExit.Add_Click({
    $timer.Stop()
    $hk.Dispose()
    $icon.Visible = $false
    $icon.Dispose()
    [System.Windows.Forms.Application]::Exit()
}) | Out-Null

Sync-Check
Apply-Hotkey | Out-Null

[System.Windows.Forms.Application]::Run()

$mutex.ReleaseMutex()
