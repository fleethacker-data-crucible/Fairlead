#requires -Version 5.1
# Fairlead PowerShell module: a thin shell around the Fairlead core (src\Fairlead.Core, C# source compiled in memory
# at launch; no DLL is shipped or loaded). All logic (gateway, routing, health, translation, admin API) lives in the
# core; PowerShell does what only it can do on the workstation: read fairlead.env, compile the core, show the tray
# icon and open the browser.
Set-StrictMode -Version 3.0

. (Join-Path $PSScriptRoot 'Tray.ps1')

$script:FairleadCoreLoaded = $false
# Framework assemblies the core compiles against on Windows PowerShell 5.1. Keep in step with the <Reference> items in
# src\Fairlead.Core\Fairlead.Core.csproj (the C# 5 check build).
$script:FairleadCoreReferences = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Security.dll')
$script:FairleadCoreLoad = $null

function Get-FairleadRoot {
    <# Repository or bundle root: two levels above module\Fairlead. #>
    [CmdletBinding()]
    param()
    $r = [Environment]::GetEnvironmentVariable('FAIRLEAD_ROOT', 'Process')
    if ($r -and (Test-Path -LiteralPath $r)) { return (Resolve-Path -LiteralPath $r).Path }
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Get-FairleadCoreSource {
    <#
    .SYNOPSIS
      The core's C# source files (src\Fairlead.Core\**\*.cs, build output folders excluded), in a stable order.
      FAIRLEAD_CORE_SOURCE may point at another source folder (development).
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot))
    $dir = [Environment]::GetEnvironmentVariable('FAIRLEAD_CORE_SOURCE', 'Process')
    if (-not $dir) { $dir = Join-Path $Root 'src\Fairlead.Core' }
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw ('The Fairlead core source folder ' + $dir + ' was not found. Extract the whole release bundle (Fairlead-<version>.zip) and run Launch-Fairlead.bat from it.')
    }
    $dir = (Resolve-Path -LiteralPath $dir).Path
    $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.cs' -Recurse -File |
        Where-Object { $_.FullName.Substring($dir.Length) -notmatch '(?i)[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($files.Count -eq 0) { throw ('No core source (*.cs) under ' + $dir + '. Extract the whole release bundle again.') }
    return [pscustomobject]@{ Directory = $dir; Files = $files }
}

function Import-FairleadCore {
    <#
    .SYNOPSIS
      Compiles the Fairlead core from source, in memory, and loads it into this session.
    .DESCRIPTION
      The core ships as C# source (src\Fairlead.Core). The workstation's application control blocks loading custom
      DLL files, so nothing is loaded from disk: Windows PowerShell 5.1 compiles the files with Add-Type and the
      inbox .NET Framework 4.8 compiler (C# 5), referencing framework assemblies only. This takes a few seconds per
      launch; the time is kept in Get-FairleadCoreLoadInfo. PowerShell 7 compiles the same files with its own
      compiler. Calling it again in the same session does nothing.
    #>
    [CmdletBinding()]
    param([string]$Root = (Get-FairleadRoot))
    if ($script:FairleadCoreLoaded -or ('Fairlead.Core.Facade' -as [type])) { $script:FairleadCoreLoaded = $true; return }
    if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        throw ('PowerShell is running in ' + $ExecutionContext.SessionState.LanguageMode + ' mode, which does not allow compiling the Fairlead core (Add-Type). Fairlead needs FullLanguage mode; ask IT. Do not try to work around the policy.')
    }
    $source = Get-FairleadCoreSource -Root $Root
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if ($PSVersionTable.PSEdition -ne 'Core') {
            # Windows PowerShell 5.1: the inbox CodeDom compiler (C# 5), in memory.
            $cp = New-Object System.CodeDom.Compiler.CompilerParameters
            $cp.GenerateInMemory = $true
            $cp.GenerateExecutable = $false
            $cp.IncludeDebugInformation = $false
            $cp.TreatWarningsAsErrors = $false
            $cp.WarningLevel = 0   # warnings never stop a launch; the C# 5 check build treats them as errors
            $cp.CompilerOptions = '/optimize+'
            [void]$cp.ReferencedAssemblies.AddRange([string[]]$script:FairleadCoreReferences)
            Add-Type -Path $source.Files -CompilerParameters $cp -ErrorAction Stop
        } else {
            # PowerShell 7 compiles against its own .NET reference set; DPAPI (ProtectedData) ships with PowerShell 7 on Windows.
            $refs = @()
            try {
                Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop
                $refs += [System.Security.Cryptography.ProtectedData].Assembly.Location
            } catch { }
            if ($refs.Count -gt 0) { Add-Type -Path $source.Files -ReferencedAssemblies $refs -IgnoreWarnings -WarningAction SilentlyContinue -ErrorAction Stop }
            else { Add-Type -Path $source.Files -IgnoreWarnings -WarningAction SilentlyContinue -ErrorAction Stop }
        }
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match '(?i)policy|blocked|application control|0x800711C7|code integrity') {
            throw ('Windows blocked compiling the Fairlead core in memory (' + $msg + '). Fairlead ships as source and loads no DLL file; ask IT whether in-memory Add-Type compilation is allowed for standard users. Do not try to work around the policy.')
        }
        throw ('The Fairlead core source under ' + $source.Directory + ' did not compile (' + $msg + '). Extract the release bundle again; if you changed the source, keep it to C# 5 and framework assemblies only.')
    }
    $watch.Stop()
    $script:FairleadCoreLoaded = $true
    $script:FairleadCoreLoad = [pscustomobject]@{
        Source       = $source.Directory
        Files        = $source.Files.Count
        Milliseconds = [int]$watch.Elapsed.TotalMilliseconds
        Compiler     = $(if ($PSVersionTable.PSEdition -ne 'Core') { 'Windows PowerShell ' + $PSVersionTable.PSVersion + ' (CodeDom, C# 5)' } else { 'PowerShell ' + $PSVersionTable.PSVersion })
    }
    Write-Verbose ('Compiled ' + $source.Files.Count + ' core source files in ' + $script:FairleadCoreLoad.Milliseconds + ' ms')
}

function Get-FairleadCoreLoadInfo {
    <# How this session loaded the core: source folder, file count, compile time (ms) and compiler; $null before Import-FairleadCore. #>
    [CmdletBinding()]
    param()
    if (-not $script:FairleadCoreLoad -and ('Fairlead.Core.Facade' -as [type])) {
        return [pscustomobject]@{ Source = $null; Files = 0; Milliseconds = 0; Compiler = 'already loaded in this session' }
    }
    return $script:FairleadCoreLoad
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

Export-ModuleMember -Function Get-FairleadRoot, Get-FairleadCoreSource, Import-FairleadCore, Get-FairleadCoreLoadInfo, Initialize-FairleadEnvironment, Start-Fairlead, Get-FairleadStatus
