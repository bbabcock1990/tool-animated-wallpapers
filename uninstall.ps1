<#
.SYNOPSIS
  Uninstall Animated Desktop Wallpapers Helper.
.DESCRIPTION
  Stops the wallpaper, removes the start-at-login entry, cleans up any legacy
  calendar refresh task + tray (from older installs), and deletes the install
  folder. Restores the normal Windows desktop. The current build hosts the tray
  and data refresh inside HtmlWallpaper.exe, so stopping that one process already
  removes the tray.

  Run from anywhere:
    irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/uninstall.ps1 | iex
.PARAMETER InstallDir
  Install folder to remove. Default: %LOCALAPPDATA%\AnimatedDesktopWallpaper.
.PARAMETER KeepFiles
  Remove startup/tasks/tray but leave the install folder on disk.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AnimatedDesktopWallpaper'),
    [switch]$KeepFiles
)

$ErrorActionPreference = 'SilentlyContinue'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    $m" -ForegroundColor Green }

Write-Host ""
Write-Host "  Animated Desktop Wallpapers Helper - uninstall" -ForegroundColor Magenta

# Stop the wallpaper (this restores the normal desktop).
Write-Step "Stopping the wallpaper"
$exe = Join-Path $InstallDir 'HtmlWallpaper.exe'
if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList 'stop' -NoNewWindow -Wait | Out-Null
}
Get-Process HtmlWallpaper -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Write-Ok "Stopped."

# Remove start-at-login shortcut.
Write-Step "Removing start-at-login"
if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList 'autostart','off' -NoNewWindow -Wait | Out-Null
}
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'HtmlWallpaper.lnk') -Force
Write-Ok "Removed."

# Tear down any legacy calendar refresh task + tray from older (pre-module) installs.
Write-Step "Removing legacy calendar task/tray (if any)"
foreach ($tn in 'HtmlWallpaper-CalendarRefresh','HtmlWallpaper-CalendarRefresh-Logon') {
    schtasks.exe /Delete /TN $tn /F 2>&1 | Out-Null
}
foreach ($ln in 'HtmlWallpaper-CalendarTray.lnk','HtmlWallpaper-ModuleTray.lnk') {
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) $ln) -Force
}
# Kill any leftover legacy tray host process (separate powershell/wscript). The
# current design hosts the tray inside HtmlWallpaper.exe (already stopped above),
# so this only matters when upgrading from an older install.
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -in 'powershell.exe','pwsh.exe','wscript.exe' -and $_.CommandLine -and
    ($_.CommandLine -like '*Show-CalendarTray*' -or $_.CommandLine -like '*Start-CalendarTray*' -or $_.CommandLine -like '*Show-ModuleTray*')
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Ok "Done."

# Delete the install folder.
if (-not $KeepFiles) {
    Write-Step "Deleting $InstallDir"
    # Give the process a moment to release file locks.
    Start-Sleep -Milliseconds 800
    Remove-Item $InstallDir -Recurse -Force
    if (Test-Path $InstallDir) {
        Write-Host "    Some files were locked; delete $InstallDir manually after signing out." -ForegroundColor Yellow
    } else {
        Write-Ok "Deleted."
    }
}

Write-Host ""
Write-Host "  Uninstalled. Your normal desktop is back." -ForegroundColor Magenta
Write-Host ""
