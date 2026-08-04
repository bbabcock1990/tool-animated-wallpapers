<#
.SYNOPSIS
  Remove the HTML wallpaper login-startup entry.
#>
$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup "HtmlWallpaper.lnk"
if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force
    Write-Host "Startup entry removed."
} else {
    Write-Host "No startup entry found."
}
