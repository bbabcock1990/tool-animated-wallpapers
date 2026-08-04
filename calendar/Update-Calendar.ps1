<#
.SYNOPSIS
  Refresh the live-wallpaper calendar data (calendar-events.js) for *today*.

.DESCRIPTION
  A desktop wallpaper (WebView2) cannot sign in to M365 interactively, so this
  script calls the WorkIQ CLI (npx @microsoft/workiq call-function), which reads
  your Outlook/M365 calendar using your cached M365 sign-in. It converts times to
  local and emits calendar-events.js, which the wallpaper HTML loads via a
  <script> tag. No AI credits are used.

  Runs unattended from a daily Scheduled Task (see Register-CalendarTask.ps1).
  The wallpaper page reloads every 15 min to pick up the refreshed file.
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$SkipFetch,
    [switch]$VerboseLog
)

# Resolve the script's own folder robustly. $PSScriptRoot can be empty when the
# script is launched via `powershell.exe -File`, so fall back to $PSCommandPath.
if (-not $OutDir) {
    $OutDir = if ($PSScriptRoot) { $PSScriptRoot }
              elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath }
              else { (Get-Location).Path }
}

$ErrorActionPreference = 'Stop'
$rawPath = Join-Path $OutDir 'calendar-raw.json'
$jsPath  = Join-Path $OutDir 'calendar-events.js'
$logPath = Join-Path $OutDir 'update-calendar.log'

# A Scheduled Task session does not always inherit the interactive user PATH, so
# copilot, node and npx (used by the WorkIQ MCP server) may be missing. Rehydrate
# PATH from the registry (Machine + User scopes) to match an interactive shell.
$machinePath = [Environment]::GetEnvironmentVariable('PATH','Machine')
$userPath    = [Environment]::GetEnvironmentVariable('PATH','User')
$env:PATH = (@($env:PATH, $machinePath, $userPath) | Where-Object { $_ } ) -join ';'

function Write-Log($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 's'), $msg
    Add-Content -Path $logPath -Value $line
    if ($VerboseLog) { Write-Host $line }
}

try {
    $npx = (Get-Command npx.ps1 -ErrorAction Stop).Source
} catch {
    # Scheduled-task sessions may not inherit the user PATH. Fall back to the
    # standard Node.js install locations.
    $npx = $null
    $fallbacks = @(
        (Join-Path $env:ProgramFiles 'nodejs\npx.ps1'),
        (Join-Path $env:ProgramFiles 'nodejs\npx.cmd'),
        (Join-Path ${env:ProgramFiles(x86)} 'nodejs\npx.cmd')
    )
    foreach ($f in $fallbacks) { if ($f -and (Test-Path $f)) { $npx = $f; break } }
    if (-not $npx -and -not $SkipFetch) {
        Write-Log "ERROR: npx (Node.js) not found in PATH or standard locations. Aborting."
        exit 1
    }
}
if ($npx) { Write-Log "Using npx: $npx" }

# ---- Build today's date window (local day) --------------------------------
$today   = Get-Date
$dayStr  = $today.ToString('yyyy-MM-dd')
$start   = "$dayStr" + "T00:00:00"
$end     = "$dayStr" + "T23:59:59"

$graphPath = "/me/calendarView?startDateTime=$start&endDateTime=$end" +
             "&`$select=subject,start,end,location,isAllDay,isCancelled,showAs,isOnlineMeeting" +
             "&`$orderby=start/dateTime&`$top=50"

if (-not $SkipFetch) {
    if (Test-Path $rawPath) { Remove-Item $rawPath -Force }

    Write-Log "Fetching calendar for $dayStr via WorkIQ CLI ..."
    $errPath = Join-Path $OutDir 'calendar-fetch.err'
    if (Test-Path $errPath) { Remove-Item $errPath -Force }

    # Call the WorkIQ CLI directly (no AI credits). stdout is the raw Graph JSON.
    $out = & $npx -y '@microsoft/workiq@latest' call-function -u $graphPath 2> $errPath | Out-String
    $code = $LASTEXITCODE

    if ($out -and $out.Trim()) {
        Set-Content -Path $rawPath -Value $out -Encoding UTF8
    }
    if ($code -ne 0) {
        $errTxt = (Get-Content $errPath -Raw -ErrorAction SilentlyContinue)
        Write-Log "WARN: WorkIQ CLI exit $code. stderr: $errTxt"
    }
} else {
    Write-Log "SkipFetch: reusing existing $rawPath"
}

if (-not (Test-Path $rawPath)) {
    Write-Log "ERROR: raw calendar file was not produced. Keeping previous calendar-events.js."
    exit 2
}

# ---- Parse raw JSON (tolerate a few shapes the model might write) ----------
$rawText = Get-Content $rawPath -Raw
# Extract the outermost JSON object if the model added stray text.
$firstBrace = $rawText.IndexOf('{')
$lastBrace  = $rawText.LastIndexOf('}')
if ($firstBrace -ge 0 -and $lastBrace -gt $firstBrace) {
    $rawText = $rawText.Substring($firstBrace, $lastBrace - $firstBrace + 1)
}

try {
    $obj = $rawText | ConvertFrom-Json
} catch {
    Write-Log "ERROR: could not parse raw JSON: $($_.Exception.Message). Keeping previous file."
    exit 3
}

# Unwrap common envelopes: { data: { value: [...] } } or { value: [...] }.
$node = $obj
if ($null -ne $node.data)  { $node = $node.data }
$value = $node.value
if ($null -eq $value) { $value = $node }   # in case the array itself was written

if ($null -eq $value) {
    Write-Log "ERROR: no 'value' array found in response. Keeping previous file."
    exit 4
}

# ---- Convert each event to local wall-clock and a compact shape ------------
# ConvertFrom-Json may hand us either a string or an already-parsed [datetime];
# normalize both to the raw "yyyy-MM-ddTHH:mm:ss" wall-clock digits (which are UTC).
function Get-Raw([object]$v) {
    if ($null -eq $v) { return $null }
    if ($v -is [datetime]) { return $v.ToString('yyyy-MM-ddTHH:mm:ss') }
    return ([string]$v)
}
function Get-DatePart([object]$v) {
    $s = Get-Raw $v
    if ($null -eq $s -or $s.Length -lt 10) { return '' }
    return $s.Substring(0, 10)
}
function To-Local([object]$dt) {
    if ($null -eq $dt -or $null -eq $dt.dateTime) { return $null }
    $s = Get-Raw $dt.dateTime
    $parsed = [datetime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture,
              [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
    return $parsed.ToLocalTime()
}

$events = @()
foreach ($e in $value) {
    if ($e.isCancelled -eq $true) { continue }   # drop cancelled meetings

    $loc = ''
    if ($e.location -and $e.location.displayName) { $loc = [string]$e.location.displayName }

    if ($e.isAllDay -eq $true) {
        # All-day events use date-only semantics with an EXCLUSIVE end date; do not
        # timezone-shift them. Active today iff startDate <= today < endDate.
        $sDate = Get-DatePart $e.start.dateTime
        $eDate = Get-DatePart $e.end.dateTime
        if ($sDate -gt $dayStr) { continue }          # hasn't started yet
        if ($eDate -and $eDate -le $dayStr) { continue }  # already ended (end is exclusive)
        $events += [ordered]@{
            subject     = [string]$e.subject
            isAllDay    = $true
            isCancelled = $false
            showAs      = [string]$e.showAs
            isOnline    = [bool]$e.isOnlineMeeting
            location    = $loc
        }
    } else {
        $s = To-Local $e.start
        $en = To-Local $e.end
        if ($null -eq $s -or $null -eq $en) { continue }
        $events += [ordered]@{
            subject     = [string]$e.subject
            isAllDay    = $false
            isCancelled = $false
            showAs      = [string]$e.showAs
            isOnline    = [bool]$e.isOnlineMeeting
            location    = $loc
            start       = $s.ToString('yyyy-MM-ddTHH:mm:ss')
            end         = $en.ToString('yyyy-MM-ddTHH:mm:ss')
        }
    }
}

$payload = [ordered]@{
    generatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    timeZone    = [System.TimeZoneInfo]::Local.Id
    date        = $dayStr
    events      = $events
}

$json = $payload | ConvertTo-Json -Depth 6
$header = "/* Calendar data for the live wallpaper. Regenerated daily by Update-Calendar.ps1.`n" +
          "   Times are local wall-clock. Do not edit by hand. */`n"
$body = "window.CALENDAR_DATA = $json;`n"

Set-Content -Path $jsPath -Value ($header + $body) -Encoding UTF8
Remove-Item $rawPath -Force -ErrorAction SilentlyContinue
Write-Log ("SUCCESS: wrote {0} events to calendar-events.js" -f $events.Count)
