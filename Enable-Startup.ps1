<#
.SYNOPSIS
  Run a chosen HTML wallpaper automatically at every login (per-user).
.EXAMPLE
  .\Enable-Startup.ps1 -Source .\demo.html
  .\Disable-Startup.ps1        # to remove
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
if ($Source -notmatch '^https?://') { $Source = (Resolve-Path $Source).Path }

$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup "HtmlWallpaper.lnk"

$argLine = "`"$Source`""
if ($Primary) { $argLine += " --primary" }

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.Arguments = $argLine
$lnk.WorkingDirectory = Split-Path $exe
$lnk.WindowStyle = 7   # minimized
$lnk.Description = "HTML live desktop wallpaper"
$lnk.Save()

Write-Host "Startup entry created: $lnkPath"
Write-Host "It will launch at your next login."
