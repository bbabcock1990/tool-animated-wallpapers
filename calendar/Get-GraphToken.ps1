<#
.SYNOPSIS
  Acquire a Microsoft Graph access token WITHOUT the Windows WAM broker.

.DESCRIPTION
  A broker-free replacement for WorkIQ's WAM sign-in, for machines/tenants where
  WAM fails (e.g. "ApiContractViolation"). It supports two interactive flows and
  a silent refresh, and caches the refresh token (DPAPI, per-user) so the
  scheduled calendar refresh stays completely silent after the first sign-in:

    1. Browser auth-code + PKCE (loopback) - default; satisfies MFA / Conditional
       Access because sign-in happens in the system browser.
    2. Device code - fallback; no local browser needed (enter a code at
       https://microsoft.com/devicelogin). Handy over RDP.
    3. Silent refresh - uses the cached refresh token; no prompts.

  Uses the well-known public client "Microsoft Graph Command Line Tools", so NO
  app registration is required. Prints ONLY the access token to stdout; all
  prompts/diagnostics go to the host.

.PARAMETER CacheFile
  Where to store the encrypted token cache. Default: graph-token.dat next to this script.

.PARAMETER Interactive
  Allow interactive sign-in (browser/device). Off by default so unattended
  refreshes never block.

.PARAMETER Login
  Force a fresh interactive sign-in even if a cache exists (implies -Interactive).

.PARAMETER PreferBrowser
  Use the loopback browser flow first instead of device code (default is device
  code first, which is the most reliable in WAM-blocked / headless / RDP setups).

.PARAMETER ClientId
  Public client (app) ID to authenticate as. Defaults to "Microsoft Graph
  Command Line Tools", the canonical first-party Graph public client. Override
  with an app your tenant admin has consented if the default is blocked.

.PARAMETER Tenant
  Authority tenant. Defaults to 'organizations' so it works for any work/school
  account without hard-coding a tenant ID.

.EXAMPLE
  # First-time interactive sign-in (installer does this once):
  .\Get-GraphToken.ps1 -Login

.EXAMPLE
  # Silent token for unattended use (scheduled task):
  $token = .\Get-GraphToken.ps1
#>
[CmdletBinding()]
param(
    [string]$CacheFile,
    [switch]$Interactive,
    [switch]$Login,
    [switch]$PreferBrowser,
    [string]$ClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e',
    [string]$Tenant   = 'organizations'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Default public client "Microsoft Graph Command Line Tools" - a Microsoft
# first-party app pre-consented for Microsoft Graph delegated scopes in most
# tenants, so no app registration is required. Override -ClientId/-Tenant for
# locked-down tenants (e.g. ones enforcing Conditional Access "token protection",
# which requires the WAM broker and blocks all broker-free public clients).
$Scope    = 'openid profile offline_access https://graph.microsoft.com/Calendars.Read'
$AuthBase = "https://login.microsoftonline.com/$Tenant/oauth2/v2.0"

if (-not $CacheFile) {
    $root = if ($PSScriptRoot) { $PSScriptRoot }
            elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath }
            else { (Get-Location).Path }
    $CacheFile = Join-Path $root 'graph-token.dat'
}
if ($Login) { $Interactive = $true }

function B64Url([byte[]]$b) {
    [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# POST an x-www-form-urlencoded body; return @{ ok; data } or @{ ok=$false; error; desc; raw }.
function Invoke-OAuthPost($url, $body) {
    try {
        $r = Invoke-RestMethod -Method Post -Uri $url -Body $body `
                -ContentType 'application/x-www-form-urlencoded' -Headers @{ Accept = 'application/json' } -ErrorAction Stop
        return @{ ok = $true; data = $r }
    } catch {
        $detail = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $detail = $_.ErrorDetails.Message
        } elseif ($_.Exception.Response) {
            try {
                $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $detail = $sr.ReadToEnd()
            } catch { }
        }
        $eo = $null
        if ($detail) { try { $eo = $detail | ConvertFrom-Json } catch { } }
        return @{ ok = $false; error = $eo.error; desc = $eo.error_description; raw = $detail }
    }
}

function Invoke-BrowserAuthCode {
    $rng = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rng)
    $verifier  = B64Url $rng
    $challenge = B64Url ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))
    $state = [Guid]::NewGuid().ToString('N')

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $redirect = "http://localhost:$port"
        $q = [ordered]@{
            client_id             = $ClientId
            response_type         = 'code'
            redirect_uri          = $redirect
            response_mode         = 'query'
            scope                 = $Scope
            state                 = $state
            code_challenge        = $challenge
            code_challenge_method = 'S256'
            prompt                = 'select_account'
        }
        $qs = ($q.GetEnumerator() | ForEach-Object { "$($_.Key)=$([Uri]::EscapeDataString([string]$_.Value))" }) -join '&'
        $authUrl = "$AuthBase/authorize?$qs"

        Write-Host "  Opening your browser to sign in..."
        Start-Process $authUrl | Out-Null

        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (-not $listener.Pending()) {
            if ($sw.Elapsed.TotalSeconds -gt 180) { throw "Browser sign-in timed out." }
            Start-Sleep -Milliseconds 200
        }
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()
        $reader = New-Object IO.StreamReader($stream)
        $requestLine = $reader.ReadLine()

        $html  = "<!doctype html><html><head><meta charset='utf-8'><title>Signed in</title></head>" +
                 "<body style='font-family:Segoe UI,Arial;background:#111;color:#eee;text-align:center;padding-top:12%'>" +
                 "<h2>&#10003; Sign-in complete</h2><p>You can close this tab and return to the installer.</p></body></html>"
        $bodyBytes = [Text.Encoding]::UTF8.GetBytes($html)
        $head = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
        $headBytes = [Text.Encoding]::ASCII.GetBytes($head)
        $stream.Write($headBytes, 0, $headBytes.Length)
        $stream.Write($bodyBytes, 0, $bodyBytes.Length)
        $stream.Flush()
        $client.Close()

        if ($requestLine -notmatch 'GET\s+(\S+)\s+HTTP') { throw "Unexpected redirect request." }
        $query = (($matches[1]) -split '\?', 2)[1]
        $params = @{}
        foreach ($pair in ($query -split '&')) {
            $kv = $pair -split '=', 2
            if ($kv.Length -eq 2) { $params[$kv[0]] = [Uri]::UnescapeDataString($kv[1]) }
        }
        if ($params['error']) { throw "Sign-in error: $($params['error']) - $($params['error_description'])" }
        if ($params['state'] -ne $state) { throw "State mismatch (possible CSRF); aborting." }
        $code = $params['code']
        if (-not $code) { throw "No authorization code returned." }

        $tok = Invoke-OAuthPost "$AuthBase/token" @{
            client_id     = $ClientId
            grant_type    = 'authorization_code'
            code          = $code
            redirect_uri  = $redirect
            code_verifier = $verifier
            scope         = $Scope
        }
        if (-not $tok.ok) { throw "Token exchange failed: $($tok.error) - $($tok.desc)" }
        return $tok.data
    } finally {
        $listener.Stop()
    }
}

function Invoke-DeviceCode {
    $dc = Invoke-OAuthPost "$AuthBase/devicecode" @{ client_id = $ClientId; scope = $Scope }
    if (-not $dc.ok) { throw "Device code request failed: $($dc.error) - $($dc.desc)" }
    $d = $dc.data

    Write-Host ""
    Write-Host "  To sign in, open: $($d.verification_uri)" -ForegroundColor Cyan
    Write-Host "  and enter code:   $($d.user_code)" -ForegroundColor Cyan
    Write-Host ""
    try { Start-Process $d.verification_uri | Out-Null } catch { }

    $interval = [int]$d.interval; if ($interval -lt 5) { $interval = 5 }
    $deadline = (Get-Date).AddSeconds([int]$d.expires_in)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        $tok = Invoke-OAuthPost "$AuthBase/token" @{
            client_id   = $ClientId
            grant_type  = 'urn:ietf:params:oauth:grant-type:device_code'
            device_code = $d.device_code
        }
        if ($tok.ok) { return $tok.data }
        switch ($tok.error) {
            'authorization_pending' { }
            'slow_down'             { $interval += 5 }
            default                 { throw "Device sign-in failed: $($tok.error) - $($tok.desc)" }
        }
    }
    throw "Device sign-in timed out."
}

# ---- Token cache (DPAPI, per-user via ConvertFrom/To-SecureString) ----------
function Protect-Str([string]$plain) {
    ConvertFrom-SecureString (ConvertTo-SecureString $plain -AsPlainText -Force)
}
function Unprotect-Str([string]$enc) {
    $ss = ConvertTo-SecureString $enc
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
function Save-Cache($data, $fallbackRt) {
    $rt = $data.refresh_token
    if (-not $rt -and $fallbackRt) { $rt = $fallbackRt }
    $exp = (Get-Date).AddSeconds([int]$data.expires_in - 120).ToString('o')
    $obj = @{
        rt  = if ($rt) { Protect-Str $rt } else { $null }
        at  = Protect-Str $data.access_token
        exp = $exp
    }
    ($obj | ConvertTo-Json) | Set-Content -Path $CacheFile -Encoding UTF8
}
function Load-Cache {
    if (-not (Test-Path $CacheFile)) { return $null }
    try { Get-Content $CacheFile -Raw | ConvertFrom-Json } catch { $null }
}

# ---- Main -------------------------------------------------------------------
$cache = Load-Cache

# 1) Unexpired cached access token.
if (-not $Login -and $cache -and $cache.at -and $cache.exp) {
    try {
        if ((Get-Date $cache.exp) -gt (Get-Date)) { Write-Output (Unprotect-Str $cache.at); return }
    } catch { }
}

# 2) Silent refresh.
if (-not $Login -and $cache -and $cache.rt) {
    try {
        $rt = Unprotect-Str $cache.rt
        $resp = Invoke-OAuthPost "$AuthBase/token" @{
            client_id     = $ClientId
            grant_type    = 'refresh_token'
            refresh_token = $rt
            scope         = $Scope
        }
        if ($resp.ok) { Save-Cache $resp.data $rt; Write-Output $resp.data.access_token; return }
    } catch { }
}

# 3) Interactive (only when allowed).
if ($Interactive) {
    $order = if ($PreferBrowser) { @('browser', 'device') } else { @('device', 'browser') }
    $data = $null
    foreach ($m in $order) {
        try {
            $data = if ($m -eq 'device') { Invoke-DeviceCode } else { Invoke-BrowserAuthCode }
            if ($data) { break }
        } catch {
            Write-Host "  $m sign-in did not complete: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if (-not $data) { throw "Interactive sign-in failed (tried: $($order -join ', '))." }
    Save-Cache $data $null
    Write-Output $data.access_token
    return
}

throw "No valid cached token and interactive sign-in is not allowed. Run 'Update-Calendar.ps1 -Login' once."
