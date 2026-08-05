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
    [switch]$VerboseLog,
    # Calendar backend:
    #   Ics    = published .ics feed URL (no sign-in, no Graph, no WAM) - works
    #            with new/modern Outlook and WAM-blocked / non-Microsoft tenants.
    #   Graph  = broker-free device-code / browser sign-in (no Node, no WAM).
    #   WorkIQ = legacy WorkIQ CLI (WAM broker).
    #   Auto   = WorkIQ (WAM) first, then broker-free Graph (device code) as a
    #            fallback. (-Login forces Graph; a configured ICS URL forces Ics.)
    [ValidateSet('Auto', 'Ics', 'Graph', 'WorkIQ')][string]$Auth = 'Auto',
    [string]$IcsUrl,           # published calendar .ics feed URL (saved for reuse)
    [switch]$Login,            # force interactive Graph sign-in (installer runs this once)
    [switch]$PreferBrowser,    # use the browser flow first instead of device code
    [string]$GraphClientId,    # override the Graph public client (locked tenants)
    [string]$GraphTenant       # override the Graph authority tenant
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
    # npx may be absent when using the broker-free Graph backend; that is fine.
    # The WorkIQ backend below errors only if it is actually the selected backend.
}
if ($npx) { Write-Log "Using npx: $npx" }

# ---- Build today's date window (local day) --------------------------------
$today   = Get-Date
$dayStr  = $today.ToString('yyyy-MM-dd')
$start   = "$dayStr" + "T00:00:00"
$end     = "$dayStr" + "T23:59:59"
$selectQ = 'subject,start,end,location,isAllDay,isCancelled,showAs,isOnlineMeeting'

# ---- Backend implementations -----------------------------------------------
# Each Fetch-* sets $script:bvalue to a Microsoft Graph-shaped event array, or
# throws on failure so the driver can fall through to the next backend.
$tokenScript = Join-Path $OutDir 'Get-GraphToken.ps1'
$icsScript   = Join-Path $OutDir 'Get-IcsCalendar.ps1'
$cacheFile   = Join-Path $OutDir 'graph-token.dat'
$icsConfig   = Join-Path $OutDir 'calendar-source.json'
$script:bvalue = $null

# Config file may hold a published ICS URL and/or a pinned backend preference,
# so the unattended scheduled task (run with no args) reuses the right source.
$cfg = $null
if (Test-Path $icsConfig) { try { $cfg = Get-Content $icsConfig -Raw | ConvertFrom-Json } catch { } }
if (-not $IcsUrl -and $cfg) { $IcsUrl = $cfg.icsUrl }
$cfgAuth = if ($cfg) { $cfg.auth } else { $null }

function Save-Config([hashtable]$patch) {
    $o = @{}
    if ($cfg) { foreach ($p in $cfg.PSObject.Properties) { $o[$p.Name] = $p.Value } }
    foreach ($k in $patch.Keys) { $o[$k] = $patch[$k] }
    try { $o | ConvertTo-Json | Set-Content -Path $icsConfig -Encoding UTF8 } catch { }
}
if ($IcsUrl -and (-not $cfg -or $cfg.icsUrl -ne $IcsUrl)) { Save-Config @{ icsUrl = $IcsUrl } }

function Fetch-WorkIQ {
    # Primary path: WorkIQ CLI, which signs in via the Windows WAM broker.
    if (-not $npx -and -not $SkipFetch) { throw "npx (Node.js) not found for the WorkIQ/WAM backend." }
    $graphPath = "/me/calendarView?startDateTime=$start&endDateTime=$end" +
                 "&`$select=$selectQ&`$orderby=start/dateTime&`$top=50"
    if (-not $SkipFetch) {
        if (Test-Path $rawPath) { Remove-Item $rawPath -Force }
        Write-Log "Fetching calendar for $dayStr via WorkIQ CLI (WAM) ..."
        $errPath = Join-Path $OutDir 'calendar-fetch.err'
        if (Test-Path $errPath) { Remove-Item $errPath -Force }
        # Call the WorkIQ CLI directly (no AI credits). stdout is the raw Graph JSON.
        $out = & $npx -y '@microsoft/workiq@latest' call-function -u $graphPath 2> $errPath | Out-String
        $code = $LASTEXITCODE
        if ($out -and $out.Trim()) { Set-Content -Path $rawPath -Value $out -Encoding UTF8 }
        if ($code -ne 0) {
            $errTxt = (Get-Content $errPath -Raw -ErrorAction SilentlyContinue)
            throw "WorkIQ CLI exit $code. $errTxt"
        }
    } else {
        Write-Log "SkipFetch: reusing existing $rawPath"
    }
    if (-not (Test-Path $rawPath)) { throw "raw calendar file was not produced." }
    $rawText = Get-Content $rawPath -Raw
    $fb = $rawText.IndexOf('{'); $lb = $rawText.LastIndexOf('}')
    if ($fb -ge 0 -and $lb -gt $fb) { $rawText = $rawText.Substring($fb, $lb - $fb + 1) }
    $obj  = $rawText | ConvertFrom-Json
    $node = $obj
    if ($null -ne $node.data) { $node = $node.data }
    $v = $node.value
    if ($null -eq $v) { $v = $node }
    if ($null -eq $v) { throw "no events array in WorkIQ response." }
    $script:bvalue = $v
}

function Fetch-Graph {
    # Fallback path: broker-free Microsoft Graph (device code by default), then a
    # direct calendarView REST call. No Node, no WorkIQ, no WAM.
    if (-not (Test-Path $tokenScript)) { throw "Get-GraphToken.ps1 not found next to this script." }
    $gArgs = @{ CacheFile = $cacheFile; Interactive = [bool]$Login; Login = [bool]$Login; PreferBrowser = $PreferBrowser }
    if ($GraphClientId) { $gArgs.ClientId = $GraphClientId }
    if ($GraphTenant)   { $gArgs.Tenant   = $GraphTenant }
    Write-Log "Fetching calendar via broker-free Microsoft Graph (device code) ..."
    $token = & $tokenScript @gArgs
    if (-not $token) { throw "no Graph access token acquired." }
    $uri = "https://graph.microsoft.com/v1.0/me/calendarView?startDateTime=$start&endDateTime=$end" +
           "&`$select=$selectQ&`$orderby=start/dateTime&`$top=50"
    $resp = Invoke-RestMethod -Uri $uri -Method Get -Headers @{
        Authorization = "Bearer $token"
        Prefer        = 'outlook.timezone="UTC"'
    }
    $script:bvalue = $resp.value
}

function Fetch-Ics {
    # Optional path: a published .ics feed (no sign-in at all).
    if (-not $IcsUrl) { throw "-Auth Ics selected but no ICS URL configured (pass -IcsUrl once)." }
    if (-not (Test-Path $icsScript)) { throw "Get-IcsCalendar.ps1 not found next to this script." }
    Write-Log "Fetching calendar via published ICS feed ..."
    $script:bvalue = & $icsScript -Url $IcsUrl -Date $today
}

# ---- Backend order: WAM/WorkIQ primary, broker-free Graph fallback ----------
switch ($Auth) {
    'WorkIQ' { $order = @('WorkIQ') }
    'Graph'  { $order = @('Graph') }
    'Ics'    { $order = @('Ics') }
    default  {
        # Auto: explicit context wins; a pinned backend (from a prior setup)
        # goes first; otherwise WAM first then broker-free device code.
        if ($IcsUrl)                  { $order = @('Ics') }
        elseif ($Login)               { $order = @('Graph') }          # -Login = set up device code now
        elseif ($cfgAuth -eq 'Graph') { $order = @('Graph', 'WorkIQ') } # pinned to device code
        elseif ($cfgAuth -eq 'WorkIQ'){ $order = @('WorkIQ', 'Graph') }
        elseif ($npx)                 { $order = @('WorkIQ', 'Graph') } # WAM primary, device-code fallback
        else                          { $order = @('Graph') }
    }
}

$value = $null
$used  = $null
foreach ($b in $order) {
    $script:bvalue = $null
    try {
        & "Fetch-$b"
        $value = $script:bvalue
        $used  = $b
        break
    } catch {
        Write-Log "Backend $b failed: $($_.Exception.Message)"
    }
}

if ($null -eq $used) {
    Write-Log "ERROR: all calendar backends failed ($($order -join ', ')). Keeping previous calendar-events.js."
    exit 4
}
Write-Log "Backend used: $used"

# Pin the backend for future unattended refreshes: if we just set up device code
# interactively, prefer Graph next time so the 15-min task stays silent and fast.
if ($used -eq 'Graph' -and $Login -and $cfgAuth -ne 'Graph') { Save-Config @{ auth = 'Graph' } }

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
