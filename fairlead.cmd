@echo off
rem fairlead: the command line for every Fairlead capability. Run "fairlead help"; see docs\CLI.md.
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\fairlead.ps1" %*
exit /b %ERRORLEVEL%
