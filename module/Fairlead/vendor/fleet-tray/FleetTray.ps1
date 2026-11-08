#requires -Version 5.1
# FleetHacker tray icons for PowerShell tools. Dot-source this file, then:
#   $notifyIcon.Icon = Get-FleetTrayIcon -App lazarette
#   $notifyIcon.Icon = Get-FleetTrayIcon -App capstan -Status bad        # corner badge: good, busy, bad, idle
#   $notifyIcon.Icon = Get-FleetTrayDot -Status busy                     # a plain status dot
# Each icon is drawn at the size the notification area uses on this display (FleetTrayArt.cs says
# why that fixes small, blurry tray icons). If C# cannot be compiled here (Add-Type blocked), the
# functions fall back to the tool's own .ico at the right size, then to Windows' application icon.

$script:FleetTrayRoot = $PSScriptRoot

function Import-FleetTrayArt {
    <# Compiles FleetTrayArt.cs once per process. Returns $true when [FleetTray.Art] is ready. #>
    if ('FleetTray.Art' -as [type]) { return $true }
    try {
        Add-Type -AssemblyName System.Drawing, System.Windows.Forms -ErrorAction Stop
        $source = [IO.File]::ReadAllText((Join-Path $script:FleetTrayRoot 'FleetTrayArt.cs'))
        Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing -ErrorAction Stop
        return $true
    } catch {
        Write-Verbose ('FleetTray: drawn icons unavailable: ' + $_.Exception.Message)
        return $false
    }
}

function Get-FleetTraySize {
    <# The tray icon size in physical pixels: 16 at 100 % scale, 20 at 125 %, 24 at 150 %, 32 at 200 %. #>
    if (Import-FleetTrayArt) { return [FleetTray.Art]::TraySize() }
    $dpi = 96
    try {
        $v = (Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction Stop).AppliedDPI
        if ($v -ge 96) { $dpi = [int]$v }
    } catch { }
    return [int][Math]::Round(16 * $dpi / 96, [MidpointRounding]::AwayFromZero)
}

function Get-FleetTrayIcon {
    <#
    .SYNOPSIS
    A tool's tray icon at this display's tray size: a bold letter on a rounded tile in the tool's colour.
    .PARAMETER App
    lazarette, windlass, fairlead, capstan, snapshackle, phoenix, ... (FleetTrayArt.cs lists them).
    .PARAMETER Status
    An optional corner badge: good, busy, bad or idle.
    .PARAMETER FallbackPath
    The tool's own .ico, used at the right size when the drawn icon is not available.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$App,
        [ValidateSet('', 'good', 'busy', 'bad', 'idle')][string]$Status = '',
        [string]$FallbackPath,
        [int]$Size = 0
    )
    if ($Size -le 0) { $Size = Get-FleetTraySize }
    if (Import-FleetTrayArt) {
        try { return [FleetTray.Art]::ForApp($App, $Status, $Size) } catch { Write-Verbose ('FleetTray: ' + $_.Exception.Message) }
    }
    return Get-FleetTrayFallback -Path $FallbackPath -Size $Size
}

function Get-FleetTrayDot {
    <# A status dot at the tray's size. #>
    [CmdletBinding()]
    param(
        [ValidateSet('good', 'busy', 'bad', 'idle')][string]$Status = 'idle',
        [int]$Size = 0
    )
    if ($Size -le 0) { $Size = Get-FleetTraySize }
    if (Import-FleetTrayArt) { return [FleetTray.Art]::Dot([System.Drawing.Color]([FleetTray.Art]::StatusColor($Status)), $Size) }
    return [System.Drawing.SystemIcons]::Application
}

function Get-FleetTrayFallback {
    param([string]$Path, [int]$Size)
    Add-Type -AssemblyName System.Drawing
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        try { return New-Object System.Drawing.Icon($Path, $Size, $Size) } catch { }
    }
    return [System.Drawing.SystemIcons]::Application
}

function Export-FleetTrayIco {
    <# Writes a tool's multi-size .ico (16 to 48 px) to Path: the fallback file a tool ships. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$App, [Parameter(Mandatory = $true)][string]$Path)
    if (-not (Import-FleetTrayArt)) { throw 'FleetTray: C# could not be compiled here.' }
    [IO.File]::WriteAllBytes($Path, [FleetTray.Art]::AppIcoBytes($App))
}

function Save-FleetTrayPreview {
    <#
    Writes a PNG of every tool's icon at each tray size, on a light and a dark taskbar, enlarged with
    hard pixel edges so each pixel can be checked. For reviewing the art; not used at run time.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [string[]]$App = @(), [int[]]$Sizes = @(16, 20, 24, 32), [int]$Zoom = 4)
    if (-not (Import-FleetTrayArt)) { throw 'FleetTray: C# could not be compiled here.' }
    if (-not $App -or $App.Count -eq 0) { $App = [FleetTray.Art]::KnownApps() }
    $rows = @()
    foreach ($a in $App) { foreach ($s in @('', 'busy', 'bad')) { $rows += , @($a, $s) } }
    $cell = [int](($Sizes | Measure-Object -Maximum).Maximum * $Zoom + 16)
    $label = 190
    $w = [int]($label + 2 * $Sizes.Count * $cell)
    $h = [int](30 + $rows.Count * $cell)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::White)
    $font = New-Object System.Drawing.Font('Segoe UI', 9)
    $half = $Sizes.Count * $cell
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([FleetTray.Art]::LightBar)), $label, 0, $half, $h)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([FleetTray.Art]::DarkBar)), $label + $half, 0, $half, $h)
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $g.DrawString("$($Sizes[$i]) px", $font, [System.Drawing.Brushes]::Black, $label + $i * $cell + 4, 6)
        $g.DrawString("$($Sizes[$i]) px", $font, [System.Drawing.Brushes]::White, $label + $half + $i * $cell + 4, 6)
    }
    for ($r = 0; $r -lt $rows.Count; $r++) {
        $a = $rows[$r][0]; $s = $rows[$r][1]
        $y = 30 + $r * $cell
        $g.DrawString(($a + $(if ($s) { " ($s)" } else { '' })), $font, [System.Drawing.Brushes]::Black, 6, $y + 8)
        for ($i = 0; $i -lt $Sizes.Count; $i++) {
            $t = [FleetTray.Art]::DrawApp($a, $s, $Sizes[$i])
            foreach ($x0 in @($label, ($label + $half))) { $g.DrawImage($t, $x0 + $i * $cell + 4, $y + 4, $Sizes[$i] * $Zoom, $Sizes[$i] * $Zoom) }
            $t.Dispose()
        }
    }
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
