<#
.SYNOPSIS
  System-tray control to turn the wallpaper calendar overlay on/off.

.DESCRIPTION
  Adds a notification-area icon with a checkable "Calendar" item and "Exit".
  Toggling writes calendar\overlay-state.js (via Toggle-Calendar.ps1), which
  calendar-overlay.js polls, so the panel appears/disappears on every monitor
  within ~2s. The check mark stays in sync even if the state is changed elsewhere
  (e.g. the CLI). Only one tray instance runs at a time.

.EXAMPLE
  .\Show-CalendarTray.ps1              # run the tray icon (foreground / hidden window)
  .\Show-CalendarTray.ps1 -Install     # also run automatically at login
  .\Show-CalendarTray.ps1 -Uninstall   # remove the login entry
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall
)

$here      = $PSScriptRoot
$toggle    = Join-Path $here 'Toggle-Calendar.ps1'
$stateFile = Join-Path $here 'overlay-state.js'
$lnkName   = 'HtmlWallpaper-CalendarTray.lnk'
$startup   = [Environment]::GetFolderPath('Startup')
$lnkPath   = Join-Path $startup $lnkName

if ($Uninstall) {
    if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force }
    Write-Host "Removed tray startup entry."
    return
}

if ($Install) {
    $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $ps
    $lnk.Arguments  = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($MyInvocation.MyCommand.Path)`""
    $lnk.WorkingDirectory = $here
    $lnk.WindowStyle = 7
    $lnk.Description = "HtmlWallpaper calendar overlay tray toggle"
    $lnk.Save()
    Write-Host "Tray will start at login: $lnkPath"
    # Fall through and also start it now.
}

# --- Single-instance guard ---
$mutex = New-Object System.Threading.Mutex($false, 'Global\HtmlWallpaperCalendarTray')
if (-not $mutex.WaitOne(0)) {
    Write-Host "Tray already running."
    return
}

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function Get-State {
    if (Test-Path $stateFile) {
        $txt = Get-Content $stateFile -Raw
        if ($txt -match '"calendar"\s*:\s*(true|false)') { return ($matches[1] -eq 'true') }
    }
    return $true
}

# Try a themed calendar-ish icon from the shell; fall back to a stock icon.
function Get-TrayIcon {
    try {
        $exe = Join-Path (Split-Path $here -Parent) 'bin\Release\net8.0-windows\HtmlWallpaper.exe'
        if (Test-Path $exe) { return [System.Drawing.Icon]::ExtractAssociatedIcon($exe) }
    } catch {}
    return [System.Drawing.SystemIcons]::Application
}

$menu   = New-Object System.Windows.Forms.ContextMenuStrip
$miCal  = New-Object System.Windows.Forms.ToolStripMenuItem 'Calendar'
$miCal.CheckOnClick = $false
$miExit = New-Object System.Windows.Forms.ToolStripMenuItem 'Exit'
[void]$menu.Items.Add($miCal)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($miExit)

$icon = New-Object System.Windows.Forms.NotifyIcon
$icon.Icon = Get-TrayIcon
$icon.Text = 'Wallpaper calendar'
$icon.Visible = $true
$icon.ContextMenuStrip = $menu

function Sync-Check {
    $on = Get-State
    $miCal.Checked = $on
    $icon.Text = "Wallpaper calendar: $(if ($on) { 'On' } else { 'Off' })"
}
Sync-Check

$miCal.Add_Click({
    $desired = -not (Get-State)
    & $toggle $(if ($desired) { '-On' } else { '-Off' }) | Out-Null
    Sync-Check
}) | Out-Null

# Left-click the icon also toggles.
$icon.Add_MouseClick({
    param($s, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        $desired = -not (Get-State)
        & $toggle $(if ($desired) { '-On' } else { '-Off' }) | Out-Null
        Sync-Check
    }
}) | Out-Null

# Keep the check mark in sync if state is changed elsewhere (e.g. the CLI).
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({ Sync-Check }) | Out-Null
$timer.Start()

$miExit.Add_Click({
    $timer.Stop()
    $icon.Visible = $false
    $icon.Dispose()
    [System.Windows.Forms.Application]::Exit()
}) | Out-Null

[System.Windows.Forms.Application]::Run()
$mutex.ReleaseMutex()
