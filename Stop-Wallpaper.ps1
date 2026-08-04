<#
.SYNOPSIS
  Stop the HTML live wallpaper and restore the normal desktop.
#>
$procs = Get-Process HtmlWallpaper -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) { Stop-Process -Id $p.Id -Force }
    # Ask the shell to repaint the original wallpaper.
    rundll32.exe user32.dll, UpdatePerUserSystemParameters
    Write-Host "Wallpaper stopped."
} else {
    Write-Host "HtmlWallpaper is not running."
}
