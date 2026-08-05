' Start-CalendarTray.vbs
' Launches the HtmlWallpaper calendar tray with NO visible console window.
'
' wscript.exe is a windowless (GUI-subsystem) host, and WshShell.Run with an
' intWindowStyle of 0 starts PowerShell hidden from the very first instant, so
' there is no flashing/black console window that, if closed, would kill the tray.

Dim sh, fso, here, script, cmd
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

here   = fso.GetParentFolderName(WScript.ScriptFullName)
script = here & "\Show-CalendarTray.ps1"

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & script & """"

' 0 = hidden window, False = don't wait for it to exit.
sh.Run cmd, 0, False
