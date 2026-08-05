<#
.SYNOPSIS
  Read today's events from a published Outlook/M365 calendar ICS feed.

.DESCRIPTION
  A completely broker-free, Graph-free, Azure-CLI-free way to get calendar data:
  it just downloads a published .ics URL over HTTPS (no sign-in at all) and
  parses it. Works with "new" (modern) Outlook, classic Outlook, and any tenant
  that permits calendar publishing - including non-Microsoft tenants and machines
  where WAM / the broker is blocked.

  How the user gets the URL (one-time):
    New Outlook / OWA -> Settings -> Calendar -> Shared calendars ->
    "Publish a calendar" -> pick the calendar + "Can view all details" ->
    Publish -> copy the ICS link (looks like
    https://outlook.office365.com/owa/calendar/<guid>/<guid>/calendar.ics).

  Emits Graph-shaped event objects (start/end/subject/location/isAllDay/...) so
  the existing Update-Calendar.ps1 conversion + calendar-events.js writer are
  reused unchanged. Recurring meetings (DAILY/WEEKLY/MONTHLY/YEARLY with
  INTERVAL/BYDAY/COUNT/UNTIL/EXDATE) are expanded for the target day.

.PARAMETER Url
  The published .ics feed URL.

.PARAMETER Date
  Target local day (default: today).

.OUTPUTS
  An array of PSCustomObjects mimicking Microsoft Graph calendarView items.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Url,
    [datetime]$Date = (Get-Date).Date
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---- Download + unfold (RFC 5545: a CRLF followed by space/TAB is a fold) ----
$raw = (Invoke-WebRequest -Uri $Url -UseBasicParsing -MaximumRedirection 5).Content
if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }
$raw = $raw -replace "`r`n", "`n"
$raw = [regex]::Replace($raw, "`n[ \t]", "")   # unfold continued lines

# ---- Timezone resolver (published Outlook feeds use Windows tz ids) ----------
$ianaToWin = @{
    'UTC'                = 'UTC'
    'America/Los_Angeles'= 'Pacific Standard Time'
    'America/Denver'     = 'Mountain Standard Time'
    'America/Chicago'    = 'Central Standard Time'
    'America/New_York'   = 'Eastern Standard Time'
    'Europe/London'      = 'GMT Standard Time'
    'Europe/Paris'       = 'W. Europe Standard Time'
    'Europe/Berlin'      = 'W. Europe Standard Time'
    'Asia/Kolkata'       = 'India Standard Time'
    'Australia/Sydney'   = 'AUS Eastern Standard Time'
}
function Resolve-Zone([string]$tzid) {
    if (-not $tzid) { return [TimeZoneInfo]::Local }
    $t = $tzid.Trim('"')
    foreach ($cand in @($t, $ianaToWin[$t])) {
        if ($cand) { try { return [TimeZoneInfo]::FindSystemTimeZoneById($cand) } catch { } }
    }
    return [TimeZoneInfo]::Local
}

# ---- Parse one ICS date/time value into a UTC DateTime (+ allDay flag) -------
function Parse-IcsDate([string]$val, [string]$tzid) {
    # Returns @{ utc=<DateTime UTC or $null>; date=<DateTime local-midnight or $null>; allDay=<bool>; zone=<TimeZoneInfo> }
    $v = $val.Trim()
    if ($v -match '^\d{8}$') {
        # VALUE=DATE -> all-day (date-only, no time component)
        $d = [datetime]::ParseExact($v, 'yyyyMMdd', $null)
        return @{ utc = $null; date = $d.Date; allDay = $true; zone = $null }
    }
    $m = [regex]::Match($v, '^(\d{8}T\d{6})(Z)?$')
    if (-not $m.Success) { return $null }
    $naive = [datetime]::ParseExact($m.Groups[1].Value, "yyyyMMdd'T'HHmmss", $null)
    if ($m.Groups[2].Value -eq 'Z') {
        $utc  = [datetime]::SpecifyKind($naive, 'Utc')
        return @{ utc = $utc; date = $null; allDay = $false; zone = [TimeZoneInfo]::Utc }
    }
    $zone = Resolve-Zone $tzid
    $unspec = [datetime]::SpecifyKind($naive, 'Unspecified')
    $utc = [TimeZoneInfo]::ConvertTimeToUtc($unspec, $zone)
    return @{ utc = $utc; date = $null; allDay = $false; zone = $zone }
}

# ---- Split a property line "NAME;p1=v1;p2=v2:VALUE" -------------------------
function Split-Prop([string]$line) {
    $c = $line.IndexOf(':')
    if ($c -lt 0) { return $null }
    $left  = $line.Substring(0, $c)
    $value = $line.Substring($c + 1)
    $parts = $left -split ';'
    $name  = $parts[0].ToUpperInvariant()
    $params = @{}
    for ($i = 1; $i -lt $parts.Count; $i++) {
        $kv = $parts[$i] -split '=', 2
        if ($kv.Count -eq 2) { $params[$kv[0].ToUpperInvariant()] = $kv[1] }
    }
    return @{ name = $name; value = $value; params = $params }
}

function Unescape-Text([string]$s) {
    if ($null -eq $s) { return '' }
    $s -replace '\\n', "`n" -replace '\\,', ',' -replace '\\;', ';' -replace '\\\\', '\'
}

# ---- Recurrence: does an occurrence of $ev start on local date $target? ------
$dayMap = @{ SU = 0; MO = 1; TU = 2; WE = 3; TH = 4; FR = 5; SA = 6 }
function StartOfWeek([datetime]$d) { $d.Date.AddDays(-((([int]$d.DayOfWeek) + 6) % 7)) } # Monday

function Test-RecursOn($rrule, [datetime]$baseDate, [datetime]$target, $exdatesLocal) {
    if ($target -lt $baseDate.Date) { return $false }
    $R = @{}
    foreach ($tok in ($rrule -split ';')) {
        $kv = $tok -split '=', 2
        if ($kv.Count -eq 2) { $R[$kv[0].ToUpperInvariant()] = $kv[1] }
    }
    $freq     = $R['FREQ']
    $interval = if ($R['INTERVAL']) { [int]$R['INTERVAL'] } else { 1 }
    if ($interval -lt 1) { $interval = 1 }
    $count    = if ($R['COUNT']) { [int]$R['COUNT'] } else { 0 }
    $untilD   = $null
    if ($R['UNTIL']) {
        $p = Parse-IcsDate $R['UNTIL'] $null
        if ($p) { $untilD = if ($p.allDay) { $p.date } else { $p.utc.ToLocalTime().Date } }
    }
    $byday = @()
    if ($R['BYDAY']) { $byday = ($R['BYDAY'] -split ',') | ForEach-Object { $_.Substring($_.Length - 2).ToUpperInvariant() } }

    $isOcc = {
        param([datetime]$d)
        switch ($freq) {
            'DAILY'   { return ((($d - $baseDate.Date).Days % $interval) -eq 0) }
            'WEEKLY'  {
                $set = if ($byday.Count) { $byday } else { @( ($dayMap.GetEnumerator() | Where-Object { $_.Value -eq [int]$baseDate.DayOfWeek }).Key ) }
                $dn = ($dayMap.GetEnumerator() | Where-Object { $_.Value -eq [int]$d.DayOfWeek }).Key
                if ($set -notcontains $dn) { return $false }
                $wk = [math]::Floor(((StartOfWeek $d) - (StartOfWeek $baseDate)).Days / 7)
                return (($wk % $interval) -eq 0)
            }
            'MONTHLY' {
                if ($d.Day -ne $baseDate.Day) { return $false }
                $mo = ($d.Year - $baseDate.Year) * 12 + ($d.Month - $baseDate.Month)
                return (($mo % $interval) -eq 0)
            }
            'YEARLY'  {
                if ($d.Month -ne $baseDate.Month -or $d.Day -ne $baseDate.Day) { return $false }
                return ((($d.Year - $baseDate.Year) % $interval) -eq 0)
            }
            default   { return $false }
        }
    }

    if ($untilD -and $target -gt $untilD) { return $false }

    # COUNT: walk occurrences from base to target, honoring the cap.
    if ($count -gt 0) {
        $seen = 0; $cur = $baseDate.Date; $guard = 0
        while ($cur -le $target -and $guard -lt 40000) {
            $guard++
            if (& $isOcc $cur) {
                $seen++
                if ($seen -gt $count) { return $false }
                if ($cur -eq $target) { break }
            }
            $cur = $cur.AddDays(1)
        }
    }

    if (-not (& $isOcc $target)) { return $false }
    if ($exdatesLocal -contains $target.ToString('yyyy-MM-dd')) { return $false }
    return $true
}

# ---- Walk VEVENT blocks -----------------------------------------------------
$lines = $raw -split "`n"
$targetDate  = $Date.Date
$dayStart    = $targetDate
$dayEnd      = $targetDate.AddDays(1)

$results = New-Object System.Collections.Generic.List[object]
$in = $false
$cur = $null

function New-GraphEvent($subject, $loc, $allDay, $startObj, $endObj) {
    [pscustomobject]@{
        subject         = $subject
        location        = [pscustomobject]@{ displayName = $loc }
        isAllDay        = $allDay
        isCancelled     = $false
        showAs          = 'busy'
        isOnlineMeeting = $false
        start           = $startObj
        end             = $endObj
    }
}

foreach ($line in $lines) {
    if ($line -eq 'BEGIN:VEVENT') { $in = $true; $cur = @{ params = @{}; exdates = @() }; continue }
    if ($line -eq 'END:VEVENT') {
        $in = $false
        if (-not $cur) { continue }
        if ($cur.status -eq 'CANCELLED') { continue }
        if (-not $cur.dtstart) { continue }

        $sp = Parse-IcsDate $cur.dtstart $cur.tzstart
        if (-not $sp) { continue }
        $ep = if ($cur.dtend) { Parse-IcsDate $cur.dtend $cur.tzend } else { $null }
        $subject = Unescape-Text $cur.summary
        $loc     = Unescape-Text $cur.location

        if ($sp.allDay) {
            # All-day: date-only, EXCLUSIVE end (Graph semantics). Emit as-is;
            # Update-Calendar decides if it is active on the target day.
            $sDate = $sp.date
            $eDate = if ($ep -and $ep.allDay) { $ep.date } else { $sDate.AddDays(1) }
            if ($cur.rrule) {
                if (-not (Test-RecursOn $cur.rrule $sDate $targetDate $cur.exdates)) { continue }
                $sDate = $targetDate; $eDate = $targetDate.AddDays(1)
            }
            $results.Add( (New-GraphEvent $subject $loc $true `
                @{ dateTime = $sDate.ToString('yyyy-MM-ddT00:00:00') } `
                @{ dateTime = $eDate.ToString('yyyy-MM-ddT00:00:00') }) )
            continue
        }

        # Timed event.
        $duration = if ($ep -and $ep.utc) { $ep.utc - $sp.utc } else { [TimeSpan]::FromMinutes(30) }
        $baseLocalDate = $sp.utc.ToLocalTime().Date

        if ($cur.rrule) {
            if (-not (Test-RecursOn $cur.rrule $baseLocalDate $targetDate $cur.exdates)) { continue }
            # Rebuild the occurrence at the same wall-clock time in the event's zone.
            $zone = if ($sp.zone) { $sp.zone } else { [TimeZoneInfo]::Local }
            $tod  = [TimeZoneInfo]::ConvertTimeFromUtc($sp.utc, $zone).TimeOfDay
            $occLocal = [datetime]::SpecifyKind($targetDate.Add($tod), 'Unspecified')
            $occUtc   = [TimeZoneInfo]::ConvertTimeToUtc($occLocal, $zone)
            $startUtc = $occUtc
        } else {
            $startUtc = $sp.utc
        }
        $endUtc = $startUtc + $duration

        # Keep it only if it overlaps the target local day.
        $sLocal = $startUtc.ToLocalTime(); $eLocal = $endUtc.ToLocalTime()
        if ($sLocal -ge $dayEnd -or $eLocal -le $dayStart) { continue }

        $results.Add( (New-GraphEvent $subject $loc $false `
            @{ dateTime = $startUtc.ToString("yyyy-MM-ddTHH:mm:ss") + 'Z' } `
            @{ dateTime = $endUtc.ToString("yyyy-MM-ddTHH:mm:ss") + 'Z' }) )
        continue
    }
    if (-not $in) { continue }

    $p = Split-Prop $line
    if (-not $p) { continue }
    switch ($p.name) {
        'SUMMARY'  { $cur.summary  = $p.value }
        'LOCATION' { $cur.location = $p.value }
        'STATUS'   { $cur.status   = $p.value.ToUpperInvariant() }
        'RRULE'    { $cur.rrule    = $p.value }
        'DTSTART'  { $cur.dtstart  = $p.value; $cur.tzstart = $p.params['TZID'] }
        'DTEND'    { $cur.dtend    = $p.value; $cur.tzend   = $p.params['TZID'] }
        'EXDATE'   {
            foreach ($x in ($p.value -split ',')) {
                $xp = Parse-IcsDate $x $p.params['TZID']
                if ($xp) {
                    $d = if ($xp.allDay) { $xp.date } else { $xp.utc.ToLocalTime().Date }
                    $cur.exdates += $d.ToString('yyyy-MM-dd')
                }
            }
        }
    }
}

,$results.ToArray()
