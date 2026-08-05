<#
.SYNOPSIS
  Set an HTML file or URL as your live Windows desktop wallpaper.
.EXAMPLE
  .\Set-Wallpaper.ps1 -Source .\demo.html
  .\Set-Wallpaper.ps1 -Source https://example.com -Primary
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [switch]$Primary
)

# Locate HtmlWallpaper.exe in either the installed layout (exe next to this
# script) or a dev build tree (bin\Release\...).
$exe = @(
    (Join-Path $PSScriptRoot "HtmlWallpaper.exe")
    (Join-Path $PSScriptRoot "bin\Release\net8.0-windows\HtmlWallpaper.exe")
    (Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\HtmlWallpaper.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    Write-Error "HtmlWallpaper.exe not found next to this script or in bin\Release. Build first (dotnet build -c Release) or run install.ps1."
    exit 1
}

# Resolve local file paths to absolute; leave URLs untouched.
if ($Source -notmatch '^https?://') {
    $Source = (Resolve-Path $Source).Path
}

$args = @($Source)
if ($Primary) { $args += "--primary" }

Start-Process -FilePath $exe -ArgumentList $args
Write-Host "Wallpaper set to: $Source"
