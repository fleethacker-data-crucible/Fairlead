#requires -Version 5.1
<#
.SYNOPSIS
  Builds and tests Fairlead, then assembles a self-contained bundle for the workstation.
.DESCRIPTION
  Runs on a development machine with the .NET 8 SDK and Node.js 20+. The output folder dist\Fairlead
  (and dist\Fairlead-<version>.zip) needs neither: it holds the core as C# source (src\Fairlead.Core, compiled in
  memory by Windows PowerShell 5.1 at launch), the built admin UI, the PowerShell module, scripts and config
  templates. It contains no DLL: the workstation's application control blocks loading custom DLL files. Copy it to
  the workstation and run Launch-Fairlead.bat.
.PARAMETER SkipTests
  Skip xUnit, UI unit tests and Pester.
.PARAMETER SkipWeb
  Reuse an existing web\dist instead of rebuilding the UI.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File build\Build-Fairlead.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipWeb,
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$bundle = Join-Path $dist 'Fairlead'

function Step([string]$m) { Write-Host ''; Write-Host ('== ' + $m) -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments, [string]$cwd = $root) {
    Write-Host ('> ' + $exe + ' ' + ($arguments -join ' ')) -ForegroundColor DarkGray
    Push-Location $cwd
    # Native tools (npm especially) write warnings to stderr; Windows PowerShell turns those into
    # terminating errors under 'Stop'. Judge the tool by its exit code only.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @arguments 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host $_.Exception.Message -ForegroundColor DarkYellow } else { Write-Host $_ } }
        if ($LASTEXITCODE -ne 0) { throw ($exe + ' exited with ' + $LASTEXITCODE) }
    } finally { $ErrorActionPreference = $previous; Pop-Location }
}

# Build stops the app (TechStack lifecycle rule): a running gateway would keep serving the old build. Say what stopped.
. (Join-Path $root 'scripts\Fairlead-Process.ps1')
$running = @(Get-FairleadProcesses -Root $root -IncludeHolders)
if ($running.Count -gt 0) {
    Step 'Stopping running Fairlead gateways before the build'
    foreach ($p in $running) { Write-Host ('  ' + $(if ($p.Kind -eq 'holder') { 'PowerShell window holding a Fairlead file' } else { 'Fairlead gateway' }) + ' PID ' + $p.Pid) -ForegroundColor DarkGray }
    foreach ($p in Stop-FairleadProcesses -Root $root -IncludeHolders) { Write-Host ('  stopped PID ' + $p) -ForegroundColor Yellow }
    $left = @(Get-FairleadProcesses -Root $root -IncludeHolders)
    if ($left.Count -gt 0) { throw ('Fairlead is still running as PID ' + (($left | ForEach-Object { $_.Pid }) -join ', ') + '. Close those PowerShell windows (they were opened as administrator) and run the build again.') }
}

foreach ($tool in @('dotnet', 'node', 'npm')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        if ($tool -ne 'dotnet' -and $SkipWeb) { continue }
        throw ($tool + ' is required on the build machine (not on the workstation). Install the .NET 8 SDK and Node.js 20+.')
    }
}

# The core ships as source. Check it the way the workstation will compile it: C# 5 (the .NET Framework 4.8
# compiler behind Add-Type), framework references only. The DLL this produces is never shipped or loaded.
Step 'Core source check (C# 5, framework references only)'
Run dotnet @('build', 'src/Fairlead.Core/Fairlead.Core.csproj', '-c', $Configuration, '--nologo')
if ($env:OS -eq 'Windows_NT') {
    & (Join-Path $PSScriptRoot 'Test-FairleadAddType.ps1') -Runs 1
} else {
    Write-Warning 'Not on Windows: skipped compiling the core with Windows PowerShell 5.1 Add-Type (CI does it).'
}
if (-not $SkipTests) {
    Step 'Core tests (xUnit)'
    Run dotnet @('test', 'src/Fairlead.Core.Tests/Fairlead.Core.Tests.csproj', '-c', $Configuration, '--nologo', '--logger', 'console;verbosity=minimal')
}

if (-not $SkipWeb) {
    Step 'Admin UI'
    $web = Join-Path $root 'web'
    if (Test-Path (Join-Path $web 'package-lock.json')) { Run npm @('ci', '--no-audit', '--no-fund') $web }
    else { Run npm @('install', '--no-audit', '--no-fund') $web }
    if (-not $SkipTests) {
        Run npm @('run', 'typecheck') $web
        Run npm @('test') $web
    }
    Run npm @('run', 'build') $web
}
if (-not (Test-Path (Join-Path $root 'web\dist\index.html'))) { throw 'web\dist\index.html is missing; build the UI first.' }

if (-not $SkipTests) {
    Step 'PowerShell tests (Pester)'
    $pester = Get-Module -ListAvailable Pester | Where-Object { $_.Version -ge [version]'5.0' } | Sort-Object Version -Descending | Select-Object -First 1
    if ($pester) {
        Import-Module $pester -Force
        $cfg = New-PesterConfiguration
        $cfg.Run.Path = (Join-Path $root 'tests\pester')
        $cfg.Run.Exit = $false
        $cfg.Run.PassThru = $true
        $cfg.Output.Verbosity = 'Normal'
        $result = Invoke-Pester -Configuration $cfg
        if ($result.FailedCount -gt 0) { throw ('Pester: ' + $result.FailedCount + ' failed') }
    } else {
        Write-Warning 'Pester 5 is not installed; skipping PowerShell tests (Install-Module Pester -Scope CurrentUser).'
    }
}

Step ('Assembling ' + $bundle)
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null
# Core source: every .cs file under src\Fairlead.Core except build output, same layout (Import-FairleadCore compiles it).
$coreSrc = (Resolve-Path (Join-Path $root 'src\Fairlead.Core')).Path
$coreFiles = @(Get-ChildItem -LiteralPath $coreSrc -Filter '*.cs' -Recurse -File | Where-Object { $_.FullName.Substring($coreSrc.Length) -notmatch '(?i)[\\/](bin|obj)[\\/]' })
if ($coreFiles.Count -eq 0) { throw ('No core source under ' + $coreSrc) }
foreach ($f in $coreFiles) {
    $dst = Join-Path (Join-Path $bundle 'src\Fairlead.Core') $f.FullName.Substring($coreSrc.Length).TrimStart('\', '/')
    New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $dst
}
Write-Host ('  core: ' + $coreFiles.Count + ' C# source files') -ForegroundColor DarkGray
Copy-Item (Join-Path $root 'web\dist') (Join-Path $bundle 'web\dist') -Recurse
Copy-Item (Join-Path $root 'module') (Join-Path $bundle 'module') -Recurse
Copy-Item (Join-Path $root 'scripts') (Join-Path $bundle 'scripts') -Recurse
Copy-Item (Join-Path $root 'integrations') (Join-Path $bundle 'integrations') -Recurse
New-Item -ItemType Directory -Force (Join-Path $bundle 'config') | Out-Null
Copy-Item (Join-Path $root 'config\fairlead.env.example') (Join-Path $bundle 'config')
Copy-Item (Join-Path $root 'docs') (Join-Path $bundle 'docs') -Recurse
New-Item -ItemType Directory -Path (Join-Path $bundle 'assets\brand') -Force | Out-Null
Copy-Item (Join-Path $root 'assets\brand\tray.ico') (Join-Path $bundle 'assets\brand')
foreach ($f in @('Launch-Fairlead.bat', 'README.md', 'TechStack')) { Copy-Item (Join-Path $root $f) $bundle }

$version = (Select-Xml -Path (Join-Path $root 'src\Fairlead.Core\Fairlead.Core.csproj') -XPath '//Version').Node.InnerText
$commit = ''
try { $commit = (& git -C $root rev-parse --short HEAD 2>$null) } catch { }
@{ version = $version; commit = $commit; configuration = $Configuration; core = 'source (C# 5, compiled in memory at launch)'; builtAt = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $bundle 'BUILD.json')

# The workstation blocks loading custom DLL files: the bundle must not contain any.
$dlls = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.Extension -in @('.dll', '.exe', '.pdb') })
if ($dlls.Count -gt 0) { throw ('The bundle must not contain binaries; found: ' + (($dlls | ForEach-Object { $_.FullName.Substring($bundle.Length) }) -join ', ')) }

if (-not $NoZip) {
    $zip = Join-Path $dist ('Fairlead-' + $version + '.zip')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip
    Write-Host ('Bundle: ' + $zip) -ForegroundColor Green
}
Write-Host ('Done. Copy ' + $bundle + ' to the workstation and run Launch-Fairlead.bat.') -ForegroundColor Green
