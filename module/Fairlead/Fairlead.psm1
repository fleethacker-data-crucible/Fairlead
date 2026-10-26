#requires -Version 5.1
# Fairlead PowerShell module: a thin shell around Fairlead.Core.dll. All logic (gateway, routing, health,
# translation, admin API) lives in the DLL; PowerShell does what only it can do on the workstation: read
# fairlead.env, load the DLL, show the tray icon and open the browser.
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

function Get-FairleadCoreCandidates {
    param([string]$Root)
    $desktop = $PSVersionTable.PSEdition -ne 'Core'
    $tfm = if ($desktop) { 'net48' } else { 'netstandard2.0' }
    $list = New-Object System.Collections.Generic.List[string]
    $custom = [Environment]::GetEnvironmentVariable('FAIRLEAD_CORE_DLL', 'Process')
    if ($custom) { [void]$list.Add($custom) }
    [void]$list.Add((Join-Path $Root ('lib\' + $tfm + '\Fairlead.Core.dll')))
    foreach ($cfg in @('Release', 'Debug')) {
        [void]$list.Add((Join-Path $Root ('src\Fairlead.Core\bin\' + $cfg + '\' + $tfm + '\Fairlead.Core.dll')))
    }
    return @($list.ToArray())
}

function Import-FairleadCore {
    <#
    .SYNOPSIS
      Loads Fairlead.Core.dll (net48 for Windows PowerShell 5.1, netstandard2.0 for PowerShell 7).
    .DESCRIPTION
      Looks in lib\ (release bundle) and then in the build output under src\. Newtonsoft.Json.dll must sit
      next to it. Throws with the searched paths when nothing is found.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot))
    if ($script:FairleadCoreLoaded -or ('Fairlead.Core.Facade' -as [type])) { $script:FairleadCoreLoaded = $true; return }
    $candidates = Get-FairleadCoreCandidates -Root $Root
    $dll = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $dll) {
        throw ("Fairlead.Core.dll was not found. Build it with build\Build-Fairlead.ps1, or use a release bundle. Searched:`n  " + ($candidates -join "`n  "))
    }
    $dir = Split-Path $dll -Parent
    foreach ($dep in @('Newtonsoft.Json.dll', 'System.Security.Cryptography.ProtectedData.dll')) {
        $path = Join-Path $dir $dep
        if (Test-Path -LiteralPath $path) {
            try { Add-Type -LiteralPath $path -ErrorAction Stop } catch { if ($_.Exception.Message -notmatch 'already') { throw } }
        } elseif ($dep -eq 'Newtonsoft.Json.dll' -and -not ('Newtonsoft.Json.JsonConvert' -as [type])) {
            throw ('Newtonsoft.Json.dll must sit next to ' + $dll + '. Rebuild with build\Build-Fairlead.ps1 or use a release bundle.')
        }
    }
    try {
        Add-Type -LiteralPath $dll -ErrorAction Stop
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked Fairlead.Core.dll (' + $msg + '). The workstation application-control policy does not allow this unsigned DLL. Ask IT to sign or allow it; do not try to work around the policy.')
        }
        throw
    }
    $script:FairleadCoreLoaded = $true
    Write-Verbose ('Loaded ' + $dll)
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
    if (-not $WebRoot) { Write-Warning 'No built admin UI found (web\dist). The gateway will run; build the UI with build\Build-Fairlead.ps1.' }
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
