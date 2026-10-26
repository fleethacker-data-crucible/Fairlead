#requires -Version 5.1
<#
.SYNOPSIS
  Stops every running Fairlead engine on this machine.
.DESCRIPTION
  Fairlead keeps running after its browser tab closes (it serves other tools). Stop it from the tray
  icon (Exit Fairlead), from "End session" in the browser, or with this script.
  It reads the runtime files the engine writes under logs\ (one per
  process) and stops those processes, then looks for any PowerShell still running Start-Fairlead.ps1.
  Build-Fairlead.ps1 calls it before building so the DLL is never locked.
.PARAMETER Quiet
  Print nothing unless something was stopped.
.PARAMETER All
  Also stop every other PowerShell window that has Fairlead.Core.dll loaded (test runs or module imports
  left open), which is what blocks a rebuild.
.EXAMPLE
  .\scripts\Stop-Fairlead.ps1
#>
[CmdletBinding()]
param([switch]$Quiet, [switch]$All)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Fairlead-Process.ps1')
$stopped = Stop-FairleadProcesses -Root $root -IncludeHolders:$All
if ($stopped.Count -gt 0) {
    foreach ($p in $stopped) { Write-Host ('Stopped PID ' + $p + '.') -ForegroundColor Yellow }
} elseif (-not $Quiet) {
    Write-Host 'No Fairlead engine is running.' -ForegroundColor DarkGray
}
