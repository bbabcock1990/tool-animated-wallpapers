<#
.SYNOPSIS
  Uninstall Animated Desktop Wallpapers Helper.
.DESCRIPTION
  Stops the wallpaper, removes the start-at-login entry, tears down the calendar
  refresh task + tray, and deletes the install folder. Restores the normal
  Windows desktop.

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
if (Test-Path (Join-Path $InstallDir 'Stop-Wallpaper.ps1')) {
    & (Join-Path $InstallDir 'Stop-Wallpaper.ps1')
} else {
    Get-Process HtmlWallpaper | ForEach-Object { Stop-Process -Id $_.Id -Force }
}
Write-Ok "Stopped."

# Remove start-at-login shortcut.
Write-Step "Removing start-at-login"
if (Test-Path (Join-Path $InstallDir 'Disable-Startup.ps1')) {
    & (Join-Path $InstallDir 'Disable-Startup.ps1')
}
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'HtmlWallpaper.lnk') -Force
Write-Ok "Removed."

# Tear down calendar task + tray, if present.
if (Test-Path (Join-Path $InstallDir 'calendar')) {
    Write-Step "Removing the calendar refresh task and tray"
    if (Test-Path (Join-Path $InstallDir 'calendar\Register-CalendarTask.ps1')) {
        & (Join-Path $InstallDir 'calendar\Register-CalendarTask.ps1') -Unregister
    }
    if (Test-Path (Join-Path $InstallDir 'calendar\Show-CalendarTray.ps1')) {
        & (Join-Path $InstallDir 'calendar\Show-CalendarTray.ps1') -Uninstall
    }
    # Terminate the live tray host. It runs as a powershell/pwsh/wscript process
    # whose command line references this install's tray scripts. The -Uninstall
    # call above only removes the startup shortcut; it does NOT stop the running
    # process, so without this the tray icon survives an uninstall (zombie).
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -in 'powershell.exe','pwsh.exe','wscript.exe' -and $_.CommandLine -and
        $_.CommandLine -like "*$InstallDir*" -and
        ($_.CommandLine -like '*Show-CalendarTray*' -or $_.CommandLine -like '*Start-CalendarTray*')
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Write-Ok "Removed."
}

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
