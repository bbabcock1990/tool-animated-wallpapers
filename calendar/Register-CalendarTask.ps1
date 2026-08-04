<#
.SYNOPSIS
  Register (or re-register) a recurring Scheduled Task that refreshes the wallpaper
  calendar data by running Update-Calendar.ps1 every N minutes.

.DESCRIPTION
  Uses schtasks.exe rather than the Register-ScheduledTask cmdlet: on managed
  (enterprise / policy-restricted) machines the COM registration path is often
  denied for a standard user, while schtasks.exe registers in the user's own
  task space without elevation.

.EXAMPLE
  .\Register-CalendarTask.ps1                 # every 15 min + ~2 min after logon
  .\Register-CalendarTask.ps1 -EveryMinutes 10
  .\Register-CalendarTask.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [int]$EveryMinutes = 15,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$taskName  = 'HtmlWallpaper-CalendarRefresh'
$logonName = 'HtmlWallpaper-CalendarRefresh-Logon'
$script    = Join-Path $PSScriptRoot 'Update-Calendar.ps1'

if ($Unregister) {
    schtasks.exe /Delete /TN $taskName  /F 2>&1 | Out-Host
    schtasks.exe /Delete /TN $logonName /F 2>&1 | Out-Host
    Write-Host "Removed scheduled tasks."
    return
}

if (-not (Test-Path $script)) { throw "Update-Calendar.ps1 not found next to this script." }

$tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""

# Run every N minutes, all day. A minute-based schedule (vs. daily) keeps the
# on-screen calendar current through the day and rolls over to the new day's
# events shortly after midnight without waiting for a single fixed daily run.
schtasks.exe /Create /TN $taskName /TR $tr /SC MINUTE /MO $EveryMinutes /F 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Failed to register recurring task (exit $LASTEXITCODE)." }

# Also refresh shortly after logon so a freshly booted machine gets same-day data.
# ONLOGON triggers are often blocked by enterprise policy for standard users; treat
# a failure here as non-fatal since the recurring trigger already covers the requirement.
schtasks.exe /Create /TN $logonName /TR $tr /SC ONLOGON /DELAY 0002:00 /F 2>&1 | Out-Null
$logonOk = ($LASTEXITCODE -eq 0)

Write-Host ""
Write-Host "Registered scheduled tasks:"
Write-Host "  - '$taskName' : every $EveryMinutes min"
if ($logonOk) {
    Write-Host "  - '$logonName' : ~2 min after logon"
} else {
    Write-Host "  - (logon trigger skipped: blocked by policy - recurring trigger still active)"
}
Write-Host "Run now with:  schtasks /Run /TN `"$taskName`""
