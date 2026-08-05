@echo off
REM Double-click installer for Animated Desktop Wallpapers Helper.
REM Runs install.ps1 sitting next to this file and forwards any arguments.
setlocal
echo.
echo Installing Animated Desktop Wallpapers Helper...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set ERR=%ERRORLEVEL%
echo.
if not "%ERR%"=="0" (
  echo Install exited with code %ERR%.
) else (
  echo Install complete.
)
echo.
pause
endlocal
