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

$exe = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\HtmlWallpaper.exe"
if (-not (Test-Path $exe)) {
    Write-Error "HtmlWallpaper.exe not found. Build first: dotnet build -c Release"
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
