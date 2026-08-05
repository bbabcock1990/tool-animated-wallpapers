<#
.SYNOPSIS
  One-command installer for Animated Desktop Wallpapers Helper.

.DESCRIPTION
  Installs a self-contained Windows build (no .NET required), sets the animated
  wallpaper, enables start-at-login, and can optionally enable the Outlook
  calendar module. The calendar signs in with the Windows account broker (device
  code fallback), and the running wallpaper hosts the tray + auto-refresh in
  process — no Node.js, no scheduled task, no separate tray process.

  Run it with the one-liner (no clone needed):

    irm https://raw.githubusercontent.com/bbabcock1990/Animated-Desktop-Wall-Papers-Helper/main/install.ps1 | iex

  Or, from a clone / download, run this script directly with parameters below.

.PARAMETER InstallDir
  Where to install. Default: %LOCALAPPDATA%\AnimatedDesktopWallpaper.

.PARAMETER Tag
  Release tag to install. Default: the latest release.

.PARAMETER Calendar
  Force-enable the calendar overlay without prompting.

.PARAMETER NoCalendar
  Skip the calendar overlay without prompting.

.PARAMETER NoAutostart
  Do not create a start-at-login entry.

.EXAMPLE
  .\install.ps1
.EXAMPLE
  .\install.ps1 -Calendar
.EXAMPLE
  .\install.ps1 -NoCalendar -NoAutostart
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AnimatedDesktopWallpaper'),
    [string]$Tag,
    [switch]$Calendar,
    [switch]$NoCalendar,
    [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Owner = 'bbabcock1990'
$Repo  = 'Animated-Desktop-Wall-Papers-Helper'
$AssetPattern = '*win-x64.zip'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "    $m" -ForegroundColor Yellow }

function Test-IsInteractive {
    return -not ([Environment]::UserInteractive -eq $false) -and -not [Console]::IsInputRedirected
}

function Ask-YesNo($question, $defaultNo = $true) {
    if (-not (Test-IsInteractive)) { return -not $defaultNo }
    $suffix = if ($defaultNo) { '[y/N]' } else { '[Y/n]' }
    $ans = Read-Host "$question $suffix"
    if ([string]::IsNullOrWhiteSpace($ans)) { return -not $defaultNo }
    return $ans -match '^(y|yes)$'
}

Write-Host ""
Write-Host "  Animated Desktop Wallpapers Helper - installer" -ForegroundColor Magenta
Write-Host "  https://github.com/$Owner/$Repo" -ForegroundColor DarkGray

# ---- Preflight -------------------------------------------------------------
Write-Step "Checking your system"
$os = [Environment]::OSVersion.Version
if ($os.Major -lt 10) { throw "Windows 10 or 11 is required (found $os)." }
# Windows 11 still reports OS version 10.0.x; only the build number (>= 22000) distinguishes it from Windows 10.
$winName = if ($os.Build -ge 22000) { '11' } else { '10' }
Write-Ok "Windows $winName build $($os.Build)"

# ---- WebView2 runtime ------------------------------------------------------
function Test-WebView2 {
    $guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $keys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$guid",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$guid",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$guid"
    )
    foreach ($k in $keys) {
        $pv = (Get-ItemProperty -Path $k -Name pv -ErrorAction SilentlyContinue).pv
        if ($pv -and $pv -ne '0.0.0.0') { return $true }
    }
    return $false
}

Write-Step "Checking the WebView2 runtime"
if (Test-WebView2) {
    Write-Ok "WebView2 runtime is present."
} else {
    Write-Warn2 "WebView2 runtime not found - installing..."
    $installed = $false
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        try {
            winget install --id Microsoft.EdgeWebView2Runtime -e --silent `
                --accept-package-agreements --accept-source-agreements | Out-Null
            $installed = Test-WebView2
        } catch { }
    }
    if (-not $installed) {
        # Fall back to the Evergreen bootstrapper.
        $boot = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
        Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $boot
        Start-Process $boot -ArgumentList '/silent','/install' -Wait
        $installed = Test-WebView2
    }
    if ($installed) { Write-Ok "WebView2 runtime installed." }
    else { Write-Warn2 "Could not confirm WebView2 install; the wallpaper may render black until it is installed." }
}

# ---- Download the latest self-contained build ------------------------------
Write-Step "Fetching the latest release"
$headers = @{ 'User-Agent' = 'adw-installer'; 'Accept' = 'application/vnd.github+json' }
$relUrl = if ($Tag) {
    "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag"
} else {
    "https://api.github.com/repos/$Owner/$Repo/releases/latest"
}
try {
    $rel = Invoke-RestMethod -Uri $relUrl -Headers $headers
} catch {
    throw "Could not query GitHub releases ($relUrl). $($_.Exception.Message)"
}
$asset = $rel.assets | Where-Object { $_.name -like $AssetPattern } | Select-Object -First 1
if (-not $asset) {
    throw "No '$AssetPattern' asset found on release '$($rel.tag_name)'. If releases have not been published yet, push a version tag (e.g. git tag v1.0.0; git push --tags) to build one."
}
Write-Ok "Release $($rel.tag_name) - $($asset.name) ($([math]::Round($asset.size/1MB,1)) MB)"

$zip = Join-Path $env:TEMP $asset.name
Write-Step "Downloading"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers @{ 'User-Agent' = 'adw-installer' }
Write-Ok "Saved to $zip"

# ---- Stop any running instance, then extract -------------------------------
Write-Step "Installing to $InstallDir"
Get-Process HtmlWallpaper -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 800
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$tmpEx = Join-Path $env:TEMP ("adw_extract_" + [guid]::NewGuid().ToString('N'))
Expand-Archive -Path $zip -DestinationPath $tmpEx -Force
# The archive contains a single top-level folder; flatten it into InstallDir.
$root = Get-ChildItem $tmpEx | Where-Object { $_.PSIsContainer } | Select-Object -First 1
$srcRoot = if ($root -and -not (Test-Path (Join-Path $tmpEx 'HtmlWallpaper.exe'))) { $root.FullName } else { $tmpEx }
Copy-Item -Path (Join-Path $srcRoot '*') -Destination $InstallDir -Recurse -Force
Remove-Item $tmpEx -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue

$exe = Join-Path $InstallDir 'HtmlWallpaper.exe'
if (-not (Test-Path $exe)) { throw "Install failed: HtmlWallpaper.exe not found in $InstallDir." }
Write-Ok "Installed."

# ---- Decide on the calendar overlay ----------------------------------------
$wantCalendar = $false
if ($Calendar) { $wantCalendar = $true }
elseif ($NoCalendar) { $wantCalendar = $false }
else { $wantCalendar = Ask-YesNo "Set up the Outlook calendar overlay (shows today's meetings; needs M365 sign-in)?" }

$wallpaper = Join-Path $InstallDir 'wallpaper.html'

if ($wantCalendar) {
    Write-Step "Setting up the calendar module (Microsoft 365 sign-in)"
    # The host owns module setup: `module enable calendar` signs in with MSAL
    # (Windows broker first, device-code fallback), fetches today's events, and
    # turns the overlay on. No Node.js, no WorkIQ, no scheduled task, no tray
    # process — the running wallpaper hosts the tray and refreshes on its own.
    # HtmlWallpaper.exe is a GUI (WinExe) app, so PowerShell's call operator does
    # NOT wait for it; use Start-Process -Wait -NoNewWindow so the installer
    # blocks until sign-in completes and shares this console (device-code prompt
    # and progress stay visible; the WAM dialog can parent to it).
    $cal = Start-Process -FilePath $exe -ArgumentList 'module','enable','calendar' `
        -WorkingDirectory $InstallDir -NoNewWindow -Wait -PassThru
    if ($cal.ExitCode -eq 0) { Write-Ok "Calendar module enabled." }
    else { Write-Warn2 "Calendar sign-in didn't complete. Enable it later with:  `"$exe`" module enable calendar" }
}

# ---- Launch the wallpaper --------------------------------------------------
Write-Step "Starting the wallpaper"
Start-Process -FilePath $exe -ArgumentList "`"$wallpaper`"" -WorkingDirectory $InstallDir
Start-Sleep -Seconds 2
if (Get-Process HtmlWallpaper -ErrorAction SilentlyContinue) { Write-Ok "Running." }
else { Write-Warn2 "The wallpaper process did not stay running; try launching $exe manually." }

# ---- Start at login --------------------------------------------------------
if (-not $NoAutostart) {
    Write-Step "Enabling start-at-login"
    & (Join-Path $InstallDir 'Enable-Startup.ps1') -Source $wallpaper
}

# ---- Done ------------------------------------------------------------------
Write-Host ""
Write-Host "  Done. Installed to: $InstallDir" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Manage it:" -ForegroundColor Gray
Write-Host "    Change wallpaper : `"$InstallDir\Set-Wallpaper.ps1`" -Source <file-or-url>"
Write-Host "    Stop / restore   : `"$InstallDir\Stop-Wallpaper.ps1`""
Write-Host "    Disable at login : `"$InstallDir\Disable-Startup.ps1`""
Write-Host "    Modules          : `"$exe`" module list | enable <id> | disable <id>"
if ($wantCalendar) {
    Write-Host "    Toggle calendar  : Ctrl+Alt+C  (or the tray 'Outlook Calendar' item)"
}
Write-Host "    Uninstall        : irm https://raw.githubusercontent.com/$Owner/$Repo/main/uninstall.ps1 | iex"
Write-Host ""
