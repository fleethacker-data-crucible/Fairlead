@echo off
rem Starts the Fairlead model gateway with a tray icon. Options: --open (show the admin page), --console (keep this window), --no-tray, --no-browser, --with-tab (stop when the admin tab closes), --replace
setlocal
powershell.exe -STA -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Fairlead.ps1" %*
endlocal
