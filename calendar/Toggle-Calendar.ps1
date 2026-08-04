<#
.SYNOPSIS
  Turn the wallpaper calendar overlay on or off (instantly, no reload).

.DESCRIPTION
  Writes calendar\overlay-state.js, a tiny flag file that calendar-overlay.js polls.
  The panel shows/hides within ~2s on every monitor. This does NOT change which
  wallpaper is running and does NOT stop the refresh task (see Register-CalendarTask.ps1).

.EXAMPLE
  .\Toggle-Calendar.ps1            # flip current state
  .\Toggle-Calendar.ps1 -On
  .\Toggle-Calendar.ps1 -Off
#>
[CmdletBinding()]
param(
    [switch]$On,
    [switch]$Off
)

$stateFile = Join-Path $PSScriptRoot 'overlay-state.js'

# Read current state (default ON if missing/unparseable).
$current = $true
if (Test-Path $stateFile) {
    $txt = Get-Content $stateFile -Raw
    if ($txt -match '"calendar"\s*:\s*(true|false)') { $current = ($matches[1] -eq 'true') }
}

if     ($On)  { $new = $true }
elseif ($Off) { $new = $false }
else          { $new = -not $current }

$json = if ($new) { 'true' } else { 'false' }
$content = @"
/* Runtime UI state for the wallpaper overlays. Written by Toggle-Calendar.ps1 and
   the tray helper (Show-CalendarTray.ps1); read (polled) by calendar-overlay.js. */
window.OVERLAY_STATE = { "calendar": $json };
"@

# UTF-8 without BOM so the <script> parses cleanly in WebView2.
[System.IO.File]::WriteAllText($stateFile, $content, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Calendar overlay: $(if ($new) { 'ON' } else { 'OFF' })"
