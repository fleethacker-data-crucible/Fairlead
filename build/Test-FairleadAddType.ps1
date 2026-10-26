#requires -Version 5.1
<#
.SYNOPSIS
  Compiles the Fairlead core exactly as the launcher does on the workstation and fails on anything that would
  stop it there: C# 6+ syntax, a non-framework reference, or an assembly loaded from disk.
.DESCRIPTION
  Each run starts a fresh Windows PowerShell 5.1 (powershell.exe), imports module\Fairlead and calls
  Import-FairleadCore: Add-Type -Path src\Fairlead.Core\**\*.cs -CompilerParameters, i.e. the inbox .NET Framework
  4.8 CodeDom compiler, which accepts C# 5 only. A control snippet with C# 6 string interpolation must fail with
  the same compiler settings, proving the check would catch C# 6+ syntax. Reports the compile time of every run.
.PARAMETER Runs
  How many fresh processes to time (the first one includes cold-start costs).
#>
[CmdletBinding()]
param([int]$Runs = 3)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $exe)) { throw 'Windows PowerShell 5.1 (powershell.exe) is required for this check.' }

$probe = @'
param([string]$Root)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Root 'module\Fairlead\Fairlead.psd1') -Force
$total = [System.Diagnostics.Stopwatch]::StartNew()
Import-FairleadCore -Root $Root
$total.Stop()
$load = Get-FairleadCoreLoadInfo
$asm = [Fairlead.Core.Facade].Assembly
$fromDisk = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { -not $_.IsDynamic -and $_.Location -and $_.Location.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Location })
$refs = @($asm.GetReferencedAssemblies() | ForEach-Object { $_.Name } | Sort-Object)

# Control: the same compiler settings must reject C# 6.
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateInMemory = $true
[void]$cp.ReferencedAssemblies.AddRange([string[]]@('System.dll', 'System.Core.dll'))
$rejectsCs6 = $false
try { Add-Type -TypeDefinition 'public static class FairleadCs6Probe { public static string M(int x) { return $"x={x}"; } }' -CompilerParameters $cp -ErrorAction Stop 2>$null } catch { $rejectsCs6 = $true }

[pscustomobject]@{
    Files = $load.Files; CompileMs = $load.Milliseconds; ImportMs = [int]$total.Elapsed.TotalMilliseconds; Compiler = $load.Compiler
    PSVersion = $PSVersionTable.PSVersion.ToString(); Clr = [Environment]::Version.ToString()
    InMemory = [string]::IsNullOrEmpty($asm.Location); FromDisk = $fromDisk; References = $refs
    Version = [Fairlead.Core.Host.FairleadHost]::Version; RejectsCSharp6 = $rejectsCs6
} | ConvertTo-Json -Compress
'@
$probeFile = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-addtype-' + [guid]::NewGuid().ToString('N') + '.ps1')
Set-Content -Path $probeFile -Value $probe -Encoding UTF8
$allowed = @('mscorlib', 'System', 'System.Core', 'System.Net.Http', 'System.Security')
$results = @()
try {
    for ($i = 1; $i -le $Runs; $i++) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & $exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probeFile -Root $root 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = $previous
        $text = (@($out) | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ } }) -join "`n"
        $json = @($out) | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
        if ($code -ne 0 -or -not $json) {
            Write-Host $text
            throw ('Windows PowerShell 5.1 could not compile the core (run ' + $i + ', exit ' + $code + '). See the compiler output above.')
        }
        $r = $json | ConvertFrom-Json
        if (-not $r.InMemory) { throw 'The core assembly has a file location; it must be compiled in memory.' }
        if (@($r.FromDisk).Count -gt 0) { throw ('Assemblies were loaded from the repository: ' + (@($r.FromDisk) -join ', ')) }
        $extra = @($r.References | Where-Object { $allowed -notcontains $_ })
        if ($extra.Count -gt 0) { throw ('The core references non-allowed assemblies: ' + ($extra -join ', ')) }
        if (-not $r.RejectsCSharp6) { throw 'The control snippet with C# 6 syntax compiled: this compiler is not the C# 5 CodeDom compiler the workstation uses, so the check proves nothing.' }
        Write-Host ('Run ' + $i + ': ' + $r.Files + ' files compiled in ' + $r.CompileMs + ' ms (Import-FairleadCore ' + $r.ImportMs + ' ms) by ' + $r.Compiler + ', CLR ' + $r.Clr + '; references: ' + ($r.References -join ', '))
        $results += $r
    }
} finally {
    Remove-Item $probeFile -Force -ErrorAction SilentlyContinue
}
$times = @($results | ForEach-Object { [int]$_.CompileMs } | Sort-Object)
$median = $times[[int][math]::Floor(($times.Count - 1) / 2)]
$summary = 'Core ' + $results[0].Version + ': ' + $results[0].Files + ' C# 5 files compiled in memory by Add-Type in ' + ($times -join ' / ') + ' ms (median ' + $median + ' ms); C# 6 control rejected; framework references only.'
Write-Host $summary -ForegroundColor Green
if ($env:GITHUB_ACTIONS) { Write-Host ('::notice title=Core compile on Windows PowerShell 5.1::' + $summary) }
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ('### Core compile (Add-Type, Windows PowerShell 5.1)' + "`n`n" + $summary) -Encoding UTF8 }
