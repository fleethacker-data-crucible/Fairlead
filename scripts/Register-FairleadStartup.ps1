#requires -Version 5.1
<#
.SYNOPSIS
  Starts Fairlead when you sign in to Windows, so tools always find the gateway. No admin, no service:
  a shortcut in your own Startup folder that runs Launch-Fairlead.bat minimised.
.PARAMETER Remove
  Delete the shortcut again.
.EXAMPLE
  .\scripts\Register-FairleadStartup.ps1
.EXAMPLE
  .\scripts\Register-FairleadStartup.ps1 -Remove
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$startup = [Environment]::GetFolderPath('Startup')
$link = Join-Path $startup 'Fairlead.lnk'
if ($Remove) {
    if (Test-Path -LiteralPath $link) {
        if ($PSCmdlet.ShouldProcess($link, 'Remove startup shortcut')) { Remove-Item -LiteralPath $link -Force; Write-Host 'Fairlead no longer starts at sign-in.' -ForegroundColor Green }
    } else { Write-Host 'There is no Fairlead startup shortcut.' -ForegroundColor DarkGray }
    return
}
if (-not $PSCmdlet.ShouldProcess($link, 'Create startup shortcut')) { return }
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($link)
$s.TargetPath = Join-Path $root 'Launch-Fairlead.bat'
$s.WorkingDirectory = $root
$s.WindowStyle = 7   # minimised; the launcher hides its console once the tray icon is up
$s.Description = 'Fairlead model gateway'
$ico = Join-Path $root 'assets\brand\tray.ico'
if (Test-Path -LiteralPath $ico) { $s.IconLocation = $ico }
$s.Save()
Write-Host ('Fairlead will start at sign-in (' + $link + ').') -ForegroundColor Green
