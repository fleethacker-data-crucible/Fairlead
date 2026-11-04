#requires -Version 5.1
# Opening Fairlead's admin page in an Edge/Chrome --app window (own profile, background features off, per the
# fleet tech-stack baseline), and the single-instance signal: a second launch asks the running instance to
# reopen its window instead of starting another one. No admin rights, no downloads, loopback only.

function Find-FairleadBrowser {
    <# The first Edge or Chrome executable found, or $null. -Candidates replaces the search list (tests). #>
    [CmdletBinding()]
    param([string[]]$Candidates)
    if (-not $Candidates) {
        $Candidates = @()
        foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
            if (-not $base) { continue }
            $Candidates += (Join-Path $base 'Microsoft\Edge\Application\msedge.exe')
            $Candidates += (Join-Path $base 'Google\Chrome\Application\chrome.exe')
        }
    }
    foreach ($c in $Candidates) { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return $c } }
    return $null
}

function Get-FairleadBrowserArguments {
    <# The command line for an app window: its own profile folder and the background networking, sync, extension and
       update features switched off. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Url, [Parameter(Mandatory = $true)][string]$ProfileDir)
    return @(
        ('--app=' + $Url),
        ('--user-data-dir="' + $ProfileDir + '"'),
        '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-sync',
        '--disable-background-networking', '--disable-component-update', '--disable-domain-reliability', '--no-pings'
    )
}

function Open-FairleadWindow {
    <# Opens the admin page in an Edge/Chrome app window; with neither installed, in the default browser.
       Returns $true for an app window. Never throws: a failed open must not stop the tray. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Url, [string]$ProfileDir, [string]$Browser)
    try {
        if (-not $Browser) { $Browser = Find-FairleadBrowser }
        if ($Browser -and $ProfileDir) {
            [void](New-Item -ItemType Directory -Force -Path $ProfileDir -ErrorAction SilentlyContinue)
            Start-Process -FilePath $Browser -ArgumentList (Get-FairleadBrowserArguments -Url $Url -ProfileDir $ProfileDir)
            return $true
        }
        Start-Process $Url
    } catch {
        Write-Warning ('Could not open the Fairlead window: ' + $_.Exception.Message)
    }
    return $false
}

$script:FairleadOpenSignalName = 'Local\Fairlead.OpenWindow'

function New-FairleadOpenListener {
    <# Held by the running instance (its tray) for its whole life. #>
    [CmdletBinding()]
    param([string]$Name = $script:FairleadOpenSignalName)
    return (New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, $Name))
}

function Test-FairleadOpenRequested {
    <# $true once (and resets) when a second launch asked this instance to reopen its window. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Listener)
    return [bool]$Listener.WaitOne(0)
}

function Send-FairleadOpenRequest {
    <# Asks the running instance to reopen its window. $false when no instance is listening. #>
    [CmdletBinding()]
    param([string]$Name = $script:FairleadOpenSignalName)
    $h = $null
    try { $h = [System.Threading.EventWaitHandle]::OpenExisting($Name) } catch { return $false }
    try { [void]$h.Set(); return $true } finally { $h.Dispose() }
}

function Get-FairleadInstanceId {
    <# What /__fleethacker/id on the gateway's origin answers (tool, pid), or $null when nothing answers. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$GatewayUrl)
    try {
        $origin = ([Uri]$GatewayUrl).GetLeftPart([UriPartial]::Authority)
        $r = Invoke-RestMethod -Uri ($origin + '/__fleethacker/id') -TimeoutSec 2
        if ($r.tool -eq 'fairlead') { return $r }
    } catch { }
    return $null
}
