<#
  Azure Updates refresher (wallpaper module: azure-updates)

  Fetches the public Azure "Updates" feed, filters it to the domains/status the
  user cares about, and writes the files the wallpaper + tray read:

    data.js       window.AZUPDATES_DATA — the overlay panel's items + meta
    links.json    clickable list for the system-tray "Azure Updates" submenu
    updates.html  a standalone, fully clickable page (opens in your browser)

  It is domain-agnostic: it filters on the feed's own productCategories, so any
  user can pick their own domains in config.json. First run seeds config.json.

    powershell -File refresh.ps1                 # normal refresh (host runs this)
    powershell -File refresh.ps1 -ListDomains    # print every domain in the feed

  Runs under Windows PowerShell 5.1 (how the host launches module commands).
#>
[CmdletBinding()]
param(
  [switch]$ListDomains
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ApiUrl     = 'https://www.microsoft.com/releasecommunications/api/v2/azure'
$UpdateBase = 'https://azure.microsoft.com/en-us/updates/?id='

function Write-Utf8NoBom {
  param([string]$Path, [string]$Content)
  [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# ---- Defaults (used when config.json is absent) --------------------------------
$Defaults = [ordered]@{
  domains  = @('Compute', 'Storage', 'Networking')
  position = 'left-of-calendar'
  offsetX  = 0
  offsetY  = 0
  status   = @('Launched', 'In preview', 'Retirement')
  maxItems = 8
}

# ---- Load / seed config.json ---------------------------------------------------
$ConfigPath = Join-Path $ScriptDir 'config.json'
$cfg = [ordered]@{}
foreach ($k in $Defaults.Keys) { $cfg[$k] = $Defaults[$k] }
if (Test-Path $ConfigPath) {
  try {
    $user = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    foreach ($k in @($cfg.Keys)) {
      if ($null -ne $user.$k) { $cfg[$k] = $user.$k }
    }
  } catch {
    Write-Output "azure-updates: config.json unreadable, using defaults ($($_.Exception.Message))"
  }
} else {
  try { Write-Utf8NoBom $ConfigPath (([pscustomobject]$Defaults) | ConvertTo-Json -Depth 5) } catch {}
}

$domains  = @($cfg.domains)  | Where-Object { $_ }
$statuses = @($cfg.status)   | Where-Object { $_ }
$maxItems = [int]$cfg.maxItems
if ($maxItems -le 0) { $maxItems = 8 }

# ---- Fetch feed ----------------------------------------------------------------
$headers = @{ 'Accept' = 'application/json'; 'User-Agent' = 'HtmlWallpaper-AzureUpdates/1.0' }
$resp    = Invoke-RestMethod -Uri $ApiUrl -Headers $headers -TimeoutSec 60
$items   = @($resp.value)
if ($items.Count -eq 0) { throw 'Azure Updates feed returned no items.' }

# ---- -ListDomains helper: discover every domain a user could pick --------------
if ($ListDomains) {
  Write-Output 'Domains available in the Azure Updates feed (productCategories):'
  $items.productCategories | Where-Object { $_ } | Group-Object |
    Sort-Object Count -Descending |
    ForEach-Object { '{0,4}  {1}' -f $_.Count, $_.Name } |
    ForEach-Object { Write-Output $_ }
  return
}

# ---- Classify + filter ---------------------------------------------------------
function Get-StatusInfo {
  param($item)
  $tags  = @($item.tags)
  $title = [string]$item.title
  $isRetire = ($tags -contains 'Retirements') -or ($title -match '(?i)\bretir|\bdeprecat|end of support|end-of-life')
  if ($isRetire) { return [pscustomobject]@{ Key = 'Retirement'; Label = 'Retiring'; Class = 'retire' } }
  switch ($item.status) {
    'Launched'       { return [pscustomobject]@{ Key = 'Launched';       Label = 'GA';      Class = 'ga' } }
    'In preview'     { return [pscustomobject]@{ Key = 'In preview';     Label = 'Preview'; Class = 'preview' } }
    'In development' { return [pscustomobject]@{ Key = 'In development'; Label = 'Dev';     Class = 'dev' } }
    default          { return [pscustomobject]@{ Key = [string]$item.status; Label = [string]$item.status; Class = 'dev' } }
  }
}

function Test-DomainMatch {
  param($item, $domains)
  if ($domains.Count -eq 0) { return $true }              # empty = all domains
  $cats = @($item.productCategories)
  $prods = @($item.products)
  foreach ($d in $domains) {
    foreach ($c in $cats)  { if ($c -and ($c -ieq $d)) { return $true } }
    foreach ($p in $prods) { if ($p -and ($p -like "*$d*")) { return $true } }
  }
  return $false
}

function Get-MatchedDomains {
  param($item, $domains)
  $cats = @($item.productCategories) | Where-Object { $_ }
  if ($domains.Count -eq 0) { return @($cats | Select-Object -First 2) }
  $out = @()
  foreach ($d in $domains) {
    foreach ($c in $cats) { if ($c -ieq $d -and ($out -notcontains $c)) { $out += $c } }
  }
  if ($out.Count -eq 0) { $out = @($cats | Select-Object -First 2) }
  return @($out | Select-Object -First 3)
}

function Get-CleanTitle {
  param([string]$title)
  ($title -replace '(?i)^\s*(Generally Available|General Availability|Public Preview|Private Preview|In development|Now available|Retirement)\s*:\s*', '').Trim()
}

function Get-BestDate {
  param($item)
  if ($item.modified) { return [string]$item.modified }
  if ($item.created)  { return [string]$item.created }
  return $null
}

$rows = @()
foreach ($it in $items) {
  $si = Get-StatusInfo $it
  if (($statuses.Count -gt 0) -and ($statuses -notcontains $si.Key)) { continue }
  if (-not (Test-DomainMatch $it $domains)) { continue }

  $date = Get-BestDate $it
  $sort = if ($date) { [datetime]$date } else { [datetime]'1900-01-01' }

  $rows += [pscustomobject]@{
    id          = [string]$it.id
    title       = Get-CleanTitle ([string]$it.title)
    url         = $UpdateBase + [string]$it.id
    statusKey   = $si.Key
    statusLabel = $si.Label
    statusClass = $si.Class
    isRetire    = ($si.Class -eq 'retire')
    domains     = @(Get-MatchedDomains $it $domains)
    date        = $date
    _sort       = $sort
  }
}

# Retirements first, then newest by modified date.
$rows = @($rows | Sort-Object @{ Expression = 'isRetire'; Descending = $true }, @{ Expression = '_sort'; Descending = $true })
$top  = @($rows | Select-Object -First $maxItems)

# ---- Write data.js (overlay panel) ---------------------------------------------
$payloadItems = @($top | ForEach-Object {
  [ordered]@{
    id          = $_.id
    title       = $_.title
    url         = $_.url
    statusLabel = $_.statusLabel
    statusClass = $_.statusClass
    domains     = @($_.domains)
    date        = $_.date
  }
})
$payload = [ordered]@{
  generatedAt = (Get-Date).ToString('o')
  meta        = [ordered]@{
    domains  = @($domains)
    position = [string]$cfg.position
    offsetX  = [int]$cfg.offsetX
    offsetY  = [int]$cfg.offsetY
  }
  items       = $payloadItems
}
$dataJs = "/* Generated by the azure-updates module. Do not edit by hand. */`r`n" +
          "window.AZUPDATES_DATA = " + ($payload | ConvertTo-Json -Depth 6) + ";`r`n"
Write-Utf8NoBom (Join-Path $ScriptDir 'data.js') $dataJs

# ---- Write links.json (tray submenu) -------------------------------------------
$linkMax  = [Math]::Max($maxItems, 12)
$links    = @($rows | Select-Object -First $linkMax | ForEach-Object {
  [ordered]@{ title = $_.title; url = $_.url; status = $_.statusLabel }
})
Write-Utf8NoBom (Join-Path $ScriptDir 'links.json') (($links | ConvertTo-Json -Depth 4))

# ---- Write updates.html (standalone clickable page) ----------------------------
function HtmlEncode { param([string]$s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
$scopeText = if ($domains.Count) { ($domains -join ' &middot; ') } else { 'All domains' }
$rowsHtml  = ($rows | Select-Object -First $linkMax | ForEach-Object {
  $badge = HtmlEncode $_.statusLabel
  $doms  = HtmlEncode (@($_.domains) -join ', ')
  $t     = HtmlEncode $_.title
  $u     = HtmlEncode $_.url
  "<li class=""r $($_.statusClass)""><a href=""$u"" target=""_blank"" rel=""noopener""><span class=""b"">$badge</span><span class=""d"">$doms</span><span class=""t"">$t</span></a></li>"
}) -join "`n"
$html = @"
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Azure Updates</title>
<style>
:root{color-scheme:dark}
body{margin:0;background:#0a1020;color:#eaf1ff;font:15px/1.5 "Segoe UI",system-ui,sans-serif}
.wrap{max-width:820px;margin:0 auto;padding:28px 20px}
h1{font-size:20px;letter-spacing:.02em;margin:0 0 2px}
.sub{opacity:.55;font-size:13px;text-transform:uppercase;letter-spacing:.14em;margin-bottom:20px}
ul{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:8px}
a{display:flex;align-items:center;gap:12px;text-decoration:none;color:inherit;
  background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:12px;padding:12px 14px}
a:hover{background:rgba(120,170,255,.10);border-color:rgba(120,180,255,.4)}
.b{font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;padding:2px 9px;border-radius:999px;white-space:nowrap}
.ga .b{background:#3fbf7f;color:#04120a}.preview .b{background:#ffc45c;color:#1a1204}
.dev .b{background:rgba(120,180,255,.22);color:#cfe1ff}.retire .b{background:#ff6b6b;color:#180404}
.d{font-size:12px;opacity:.6;white-space:nowrap}.t{flex:1;min-width:0}
.foot{margin-top:22px;opacity:.4;font-size:12px}
</style></head><body><div class="wrap">
<h1>Azure Updates</h1><div class="sub">$scopeText</div>
<ul>
$rowsHtml
</ul>
<div class="foot">Updated $((Get-Date).ToString('MMM d, yyyy h:mm tt')) &middot; source: azure.microsoft.com/updates</div>
</div></body></html>
"@
Write-Utf8NoBom (Join-Path $ScriptDir 'updates.html') $html

# ---- Start Menu shortcut to updates.html (created once; safe fallback to open) --
try {
  $programs = [Environment]::GetFolderPath('Programs')
  $lnk = Join-Path $programs 'Azure Updates.lnk'
  if (-not (Test-Path $lnk)) {
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = (Join-Path $ScriptDir 'updates.html')
    $sc.Description = 'Azure Updates (from the wallpaper module)'
    $sc.Save()
  }
} catch { }

Write-Output "azure-updates: wrote $($top.Count) item(s) to panel, $([Math]::Min($rows.Count,$linkMax)) link(s). Domains: $(if($domains.Count){$domains -join ', '}else{'all'})."
