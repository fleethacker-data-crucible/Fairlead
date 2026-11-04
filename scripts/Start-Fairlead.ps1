#requires -Version 5.1
<#
.SYNOPSIS
  Starts Fairlead: loads config\fairlead.env, starts the model gateway on 127.0.0.1:8776, shows the tray icon
  and, on first run, opens the one-time setup in the browser. Launch-Fairlead.bat calls this.
.PARAMETER Open
  Open the admin page even when setup is already done. Also: Launch-Fairlead.bat --open
.PARAMETER KeepConsole
  Leave this window visible (diagnosing startup problems). Also: --console
.PARAMETER NoTray
  No tray icon; the console stays and Ctrl+C stops Fairlead. Also: --no-tray
.PARAMETER Replace
  If Fairlead is already running, stop it and start a new one. Without it, a second launch reuses the running
  instance and just reopens its window. Also: --replace
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$Open,
    [switch]$NoBrowser,
    [switch]$KeepConsole,
    [switch]$NoTray,
    [switch]$Replace,
    [string]$WebRoot,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
foreach ($a in @($Rest)) {
    switch -Regex ($a) {
        '^--?console$' { $KeepConsole = $true }
        '^--?open$' { $Open = $true }
        '^--?no-browser$' { $NoBrowser = $true }
        '^--?no-tray$' { $NoTray = $true; $KeepConsole = $true }
        '^--?stay$' { }   # the default for Fairlead; accepted so fleet muscle memory works
        '^--?replace$' { $Replace = $true }
        default { if ($a) { Write-Warning "Ignoring unknown option $a" } }
    }
}
Import-Module (Join-Path $PSScriptRoot '..\module\Fairlead\Fairlead.psd1') -Force
. (Join-Path $PSScriptRoot 'Fairlead-Process.ps1')
$root = Split-Path $PSScriptRoot -Parent
$running = @(Get-FairleadProcesses -Root $root)
if ($running.Count -gt 0) {
    $where = ($running | ForEach-Object { if ($_.Gateway) { $_.Gateway + ' (PID ' + $_.Pid + ')' } else { 'PID ' + $_.Pid } }) -join ', '
    if ($Replace) {
        Write-Host ('Fairlead is already running: ' + $where + '. Replacing it.') -ForegroundColor Yellow
        foreach ($p in Stop-FairleadProcesses -Root $root) { Write-Host ('  stopped PID ' + $p) -ForegroundColor DarkGray }
    } else {
        # Single instance: reuse the one that is running. Its tray reopens the window; nothing is stopped or started.
        $answers = $false
        foreach ($r in $running) { if ($r.Gateway -and (Get-FairleadInstanceId -GatewayUrl $r.Gateway)) { $answers = $true } }
        if (Send-FairleadOpenRequest) {
            Write-Host ('Fairlead is already running: ' + $where + '. Reopening its window.') -ForegroundColor Cyan
        } else {
            Write-Host ('Fairlead is already running: ' + $where + '.') -ForegroundColor Yellow
            if ($answers) { Write-Host '  It answers on its port but has no tray icon to reopen, so it was started with --no-tray. Use --replace to restart it with one.' -ForegroundColor DarkGray }
            else { Write-Host '  It is not answering yet. Wait a few seconds and start again, or use --replace.' -ForegroundColor DarkGray }
        }
        exit 0
    }
}
try {
    $params = @{ Port = $Port; Open = $Open; NoBrowser = $NoBrowser; KeepConsole = $KeepConsole; NoTray = $NoTray }
    if ($WebRoot) { $params['WebRoot'] = $WebRoot }
    Start-Fairlead @params
} catch {
    try { [void][Fairlead.Core.Facade]::SetConsoleVisible($true) } catch { }
    Write-Host ''
    Write-Host ('Fairlead could not start: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'This window stays open so you can read the error. Press Enter to close.' -ForegroundColor DarkGray
    [void](Read-Host)
    exit 1
}
