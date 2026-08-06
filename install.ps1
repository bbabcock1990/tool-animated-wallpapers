<#
.SYNOPSIS
  One-command installer for Animated Desktop Wallpapers Helper.

.DESCRIPTION
  Installs a self-contained Windows build (no .NET required), sets the animated
  wallpaper, enables start-at-login, and can optionally enable the Outlook
  calendar module. The calendar can sign in via WorkIQ (reuses your existing
  Windows M365 sign-in — best for locked-down tenants like microsoft.com) or via
  the Windows account broker / MSAL (with a device-code fallback). The running
  wallpaper hosts the tray + auto-refresh in process — no scheduled task and no
  separate tray process.

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

.PARAMETER CalendarAuth
  Calendar sign-in method: Auto (WorkIQ then MSAL/WAM), WorkIQ, or Msal.
  Default when the calendar is enabled non-interactively is Auto.

.EXAMPLE
  .\install.ps1
.EXAMPLE
  .\install.ps1 -Calendar
.EXAMPLE
  .\install.ps1 -Calendar -CalendarAuth WorkIQ
.EXAMPLE
  .\install.ps1 -NoCalendar -NoAutostart
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AnimatedDesktopWallpaper'),
    [string]$Tag,
    [switch]$Calendar,
    [switch]$NoCalendar,
    [ValidateSet('Auto','WorkIQ','Msal')][string]$CalendarAuth,
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

# Present a numbered menu and return the chosen value. Non-interactive sessions
# get the default without prompting.
function Ask-Choice($question, $options, $default) {
    if (-not (Test-IsInteractive)) { return $default }
    Write-Host ""
    Write-Host "  $question" -ForegroundColor Gray
    for ($i = 0; $i -lt $options.Count; $i++) {
        $mark = if ($options[$i].Value -eq $default) { ' (default)' } else { '' }
        Write-Host ("    {0}) {1}{2}" -f ($i + 1), $options[$i].Label, $mark)
    }
    $ans = Read-Host "  Choose 1-$($options.Count)"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $default }
    $n = 0
    if ([int]::TryParse($ans, [ref]$n) -and $n -ge 1 -and $n -le $options.Count) {
        return $options[$n - 1].Value
    }
    return $default
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
    Write-Step "Setting up the calendar module"

    # Choose the sign-in method. WorkIQ reuses your existing Windows M365 sign-in
    # through an app that is already approved in locked-down tenants (e.g.
    # microsoft.com, where generic Graph clients require admin consent). MSAL/WAM
    # uses the Windows broker directly (with a device-code fallback). Auto tries
    # WorkIQ first, then MSAL.
    $auth = if ($CalendarAuth) { $CalendarAuth } else {
        Ask-Choice "How should the calendar sign in to Microsoft 365?" @(
            @{ Label = 'Auto - try WorkIQ first, then Windows broker (recommended)'; Value = 'Auto' }
            @{ Label = 'WorkIQ - use my existing Windows M365 sign-in (needs Node.js; best for microsoft.com)'; Value = 'WorkIQ' }
            @{ Label = 'Windows broker / MSAL only (no Node.js)'; Value = 'Msal' }
        ) 'Auto'
    }
    Write-Ok "Sign-in method: $auth"

    # For the WorkIQ path, make sure Node.js is present and WorkIQ has a cached
    # sign-in before we ask the module to fetch. (Auto still falls back to MSAL if
    # any of this is unavailable, so failures here are non-fatal.)
    if ($auth -eq 'WorkIQ' -or $auth -eq 'Auto') {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
            Write-Warn2 "Node.js not found - installing (via winget)..."
            if (Get-Command winget -ErrorAction SilentlyContinue) {
                try {
                    winget install --id OpenJS.NodeJS.LTS -e --silent `
                        --accept-package-agreements --accept-source-agreements | Out-Null
                } catch { }
                $env:PATH = (@($env:PATH,
                    [Environment]::GetEnvironmentVariable('PATH','Machine'),
                    [Environment]::GetEnvironmentVariable('PATH','User')) | Where-Object { $_ }) -join ';'
            }
        }
        if (Get-Command node -ErrorAction SilentlyContinue) {
            Write-Step "Signing in to Microsoft 365 via WorkIQ (a window may open)"
            try { npx -y '@microsoft/workiq@latest' accept-eula 2>$null | Out-Null } catch { }
            try { npx -y '@microsoft/workiq@latest' auth login } catch { Write-Warn2 "WorkIQ sign-in did not complete; Auto will fall back to the Windows broker." }
        } elseif ($auth -eq 'WorkIQ') {
            Write-Warn2 "Node.js is unavailable, so WorkIQ can't run. Falling back to the Windows broker (MSAL)."
            $auth = 'Msal'
        }
    }

    # The host owns module setup: `module enable calendar --auth <method>` fetches
    # today's events (WorkIQ and/or MSAL per the method), turns the overlay on, and
    # schedules the in-process 15-minute refresh. No scheduled task, no separate
    # tray process — the running wallpaper hosts the tray and refreshes on its own.
    # HtmlWallpaper.exe is a GUI (WinExe) app, so PowerShell's call operator does
    # NOT wait for it; use Start-Process -Wait -NoNewWindow so the installer blocks
    # until sign-in completes and shares this console (device-code prompt/progress
    # stay visible; the WAM dialog can parent to it).
    $cal = Start-Process -FilePath $exe `
        -ArgumentList 'module','enable','calendar','--auth',$auth.ToLower() `
        -WorkingDirectory $InstallDir -NoNewWindow -Wait -PassThru
    if ($cal.ExitCode -eq 0) { Write-Ok "Calendar module enabled." }
    else { Write-Warn2 "Calendar sign-in didn't complete. Enable it later with:  `"$exe`" module enable calendar --auth $($auth.ToLower())" }
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
    # `autostart on` creates the login shortcut in-process (no helper script);
    # it doesn't spawn the wallpaper, so -Wait can't hang on a shared console.
    Start-Process -FilePath $exe -ArgumentList 'autostart','on','--source',"`"$wallpaper`"" `
        -WorkingDirectory $InstallDir -NoNewWindow -Wait | Out-Null
}

# ---- Done ------------------------------------------------------------------
Write-Host ""
Write-Host "  Done. Installed to: $InstallDir" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Manage it (all through HtmlWallpaper.exe):" -ForegroundColor Gray
Write-Host "    Change wallpaper : `"$exe`" set <file-or-url>"
Write-Host "    Stop / restore   : `"$exe`" stop"
Write-Host "    Disable at login : `"$exe`" autostart off"
Write-Host "    Modules          : `"$exe`" module list | enable <id> | disable <id>"
if ($wantCalendar) {
    Write-Host "    Toggle calendar  : Ctrl+Alt+C  (or the tray 'Outlook Calendar' item)"
}
Write-Host "    Uninstall        : irm https://raw.githubusercontent.com/$Owner/$Repo/main/uninstall.ps1 | iex"
Write-Host ""
