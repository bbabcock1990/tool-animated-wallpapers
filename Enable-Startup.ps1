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

$exe = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\HtmlWallpaper.exe"
if (-not (Test-Path $exe)) {
    Write-Error "HtmlWallpaper.exe not found. Build first: dotnet build -c Release"
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
