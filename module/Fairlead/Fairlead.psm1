#requires -Version 5.1
# Fairlead PowerShell module: a thin shell around the Fairlead engine (src\Fairlead.Core, C# 5 compiled at start
# with Add-Type). All logic (gateway, routing, health, translation, admin API) lives in the engine; PowerShell does
# what only it can do on the workstation: read fairlead.env, compile and load the engine, show the tray icon and open
# the browser.
Set-StrictMode -Version 3.0

. (Join-Path $PSScriptRoot 'Tray.ps1')

$script:FairleadCoreLoaded = $false

function Get-FairleadRoot {
    <# Repository or bundle root: two levels above module\Fairlead. #>
    [CmdletBinding()]
    param()
    $r = [Environment]::GetEnvironmentVariable('FAIRLEAD_ROOT', 'Process')
    if ($r -and (Test-Path -LiteralPath $r)) { return (Resolve-Path -LiteralPath $r).Path }
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Get-FairleadCoreSources {
    <# The engine's C# source files (src\Fairlead.Core, without build output). #>
    param([string]$Root)
    $dir = Join-Path $Root 'src\Fairlead.Core'
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { $_.FullName })
}

function Import-FairleadCore {
    <#
    .SYNOPSIS
      Compiles the Fairlead engine from src\Fairlead.Core\*.cs in memory and loads it.
    .DESCRIPTION
      The Fleet stack: C# 5 source compiled at start by Windows PowerShell 5.1 (Add-Type, the inbox .NET Framework
      compiler), framework assemblies only. No custom or third-party DLL is ever loaded.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot))
    if ($script:FairleadCoreLoaded -or ('Fairlead.Core.Facade' -as [type])) { $script:FairleadCoreLoaded = $true; return }
    $files = Get-FairleadCoreSources -Root $Root
    if ($files.Count -eq 0) { throw ('The Fairlead engine source was not found under ' + (Join-Path $Root 'src\Fairlead.Core') + '. Copy the whole Fairlead folder.') }
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateInMemory = $true
    $cp.CompilerOptions = '/optimize+'
    foreach ($r in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Security.dll')) { [void]$cp.ReferencedAssemblies.Add($r) }
    try {
        Add-Type -Path $files -CompilerParameters $cp -ErrorAction Stop
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked compiling the Fairlead engine (' + $msg + '). The application-control policy does not allow runtime C# compilation here; ask IT, do not work around the policy.')
        }
        throw ('The Fairlead engine did not compile: ' + $msg)
    }
    $script:FairleadCoreLoaded = $true
    Write-Verbose ('Compiled ' + $files.Count + ' engine files')
}

function Initialize-FairleadEnvironment {
    <#
    .SYNOPSIS
      Loads config\fairlead.env into the process environment and returns FairleadSettings.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot), [string]$EnvFile)
    Import-FairleadCore -Root $Root
    if (-not $EnvFile) { $EnvFile = Join-Path $Root 'config\fairlead.env' }
    $applied = [Fairlead.Core.Facade]::LoadEnvFile($EnvFile)
    if ($applied -gt 0) { Write-Verbose ("Applied $applied settings from $EnvFile") }
    $settings = [Fairlead.Core.Facade]::SettingsFromEnvironment($Root)
    return [pscustomobject]@{ Settings = $settings; EnvFile = $EnvFile; EnvApplied = $applied }
}

function Find-FairleadWebRoot {
    param([string]$Root)
    foreach ($p in @([Environment]::GetEnvironmentVariable('FAIRLEAD_WEBROOT', 'Process'), (Join-Path $Root 'web\dist'))) {
        if ($p -and (Test-Path -LiteralPath (Join-Path $p 'index.html'))) {
            $idx = Get-Content -LiteralPath (Join-Path $p 'index.html') -Raw
            if ($idx -notmatch '%APP_JS%') { return (Resolve-Path -LiteralPath $p).Path }
        }
    }
    return $null
}

function Start-Fairlead {
    <#
    .SYNOPSIS
      Starts the Fairlead gateway on 127.0.0.1:8776 with a tray icon, opens the admin page when setup is not
      finished (or -Open), hides the console (unless -KeepConsole) and blocks until Exit, End session or Stop-Fairlead.
    .PARAMETER NoTray
      Run without the tray icon (CI, servers, PowerShell 7 on a machine without WinForms).
    .PARAMETER AutoStop
      Let the browser tab own the engine (stop 20 s after the tab closes). Off by default: Fairlead serves other tools.
    #>
    [CmdletBinding()]
    param(
        [string]$Root = (Get-FairleadRoot),
        [int]$Port = 0,
        [switch]$Open,
        [switch]$NoBrowser,
        [switch]$KeepConsole,
        [switch]$NoTray,
        [string]$WebRoot,
        [bool]$AutoStop = $false
    )
    $envInfo = Initialize-FairleadEnvironment -Root $Root
    $settings = $envInfo.Settings
    if (-not $WebRoot) { $WebRoot = Find-FairleadWebRoot -Root $Root }
    if (-not $WebRoot) { Write-Warning 'No admin UI found (web\dist is missing from this copy). The gateway will run without its setup page.' }
    $launcher = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $launcher['powershell'] = $PSVersionTable.PSVersion.ToString()
    $launcher['edition'] = $PSVersionTable.PSEdition
    $launcher['tray'] = -not $NoTray
    $preferred = if ($Port -gt 0) { $Port } else { $settings.PreferredPort }
    $autoStopOn = $AutoStop -or $settings.AutoStop
    $hostObj = [Fairlead.Core.Facade]::CreateHost($settings, $WebRoot, $preferred, $launcher, $autoStopOn)
    $info = $hostObj.Start()
    $setupDone = $hostObj.Config.Snapshot().SetupComplete
    Write-Host ''
    Write-Host ('Fairlead is running. Point tools at ' + $info.GatewayUrl) -ForegroundColor Cyan
    if (-not $info.PortIsRegistry) { Write-Host ('  Port ' + $preferred + ' was busy, so Fairlead took ' + $info.Port + '. Tools configured for ' + $preferred + ' will not reach it.') -ForegroundColor Yellow }
    Write-Host ('  Admin: ' + $info.Url + '   Config: ' + $info.ConfigFile) -ForegroundColor DarkGray
    if ($autoStopOn) { Write-Host '  FAIRLEAD_AUTO_STOP=1: it stops when the admin tab closes.' -ForegroundColor DarkGray }
    else { Write-Host '  It keeps running after the browser closes. Stop it from the tray icon, "End session", or scripts\Stop-Fairlead.ps1.' -ForegroundColor DarkGray }
    if ($NoBrowser) { Write-Host ('  Admin link (open once): ' + $info.LaunchUrl) -ForegroundColor DarkGray }
    elseif ($Open -or -not $setupDone) { Start-Process $info.LaunchUrl }
    $useTray = -not $NoTray -and (Test-FairleadTraySupported)
    if (-not $KeepConsole -and $useTray) {
        Start-Sleep -Milliseconds 600
        [void][Fairlead.Core.Facade]::SetConsoleVisible($false)
    }
    try {
        if ($useTray) {
            $iconPath = Join-Path $Root 'assets\brand\tray.ico'
            Invoke-FairleadTray -HostObject $hostObj -LaunchUrl $info.LaunchUrl -GatewayUrl $info.GatewayUrl -IconPath $iconPath
        } else {
            # Poll so Ctrl+C in a visible console still works.
            while (-not $hostObj.WaitForStop(500)) { }
        }
    } finally {
        $hostObj.Dispose()
        [void][Fairlead.Core.Facade]::SetConsoleVisible($true)
    }
}

function Get-FairleadStatus {
    <# The status of the Fairlead running on this machine, from its public /v1/health and runtime file. #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot))
    . (Join-Path $Root 'scripts\Fairlead-Process.ps1')
    foreach ($p in Get-FairleadProcesses -Root $Root) {
        $health = $null
        if ($p.Gateway) {
            try { $health = Invoke-RestMethod -Uri ($p.Gateway + '/health') -TimeoutSec 3 } catch { }
        }
        [pscustomobject]@{ Pid = $p.Pid; Gateway = $p.Gateway; Admin = $p.Url; Kedge = $(if ($health) { $health.kedge } else { $null }); Internet = $(if ($health) { $health.internet } else { $null }) }
    }
}

Export-ModuleMember -Function Get-FairleadRoot, Import-FairleadCore, Initialize-FairleadEnvironment, Start-Fairlead, Get-FairleadStatus
