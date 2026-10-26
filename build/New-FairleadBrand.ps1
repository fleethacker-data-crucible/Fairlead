#requires -Version 5.1
<#
.SYNOPSIS
  Renders the placeholder brand assets: emblem PNGs (light and dark), the mark as PNGs, favicon.ico and tray.ico.
.DESCRIPTION
  Build-machine only. Uses headless Edge (or Chrome) to render assets\brand\emblem-source.html and
  assets\brand\mark.svg to PNG with a transparent background, then packs PNG frames into .ico files.
  When the detailed original emblem exists, drop it in as assets\brand\emblem-light.png / emblem-dark.png and
  stop running the emblem part of this script (-MarkOnly).
#>
[CmdletBinding()]
param([switch]$MarkOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$brand = Join-Path $root 'assets\brand'
$public = Join-Path $root 'web\public'
$browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome is needed on the build machine to render the brand assets.' }
Add-Type -AssemblyName System.Drawing

function Render([string]$url, [string]$out, [int]$w, [int]$h, [int]$scale = 1) {
    $profile = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-brand-' + [guid]::NewGuid().ToString('N'))
    $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--default-background-color=00000000', "--user-data-dir=$profile", "--window-size=$w,$h", "--force-device-scale-factor=$scale", "--screenshot=$out", $url)
    $p = Start-Process -FilePath $browser -ArgumentList $args -PassThru -WindowStyle Hidden -Wait
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $out)) { throw ('Rendering failed: ' + $url) }
}

function Resize([string]$src, [string]$out, [int]$size) {
    $img = [System.Drawing.Image]::FromFile($src)
    try {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($img, 0, 0, $size, $size)
        $g.Dispose()
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    } finally { $img.Dispose() }
}

function Write-Ico([string]$out, [string[]]$pngs) {
    # ICO with PNG frames (Vista+): header, one directory entry per frame, then the PNG bytes.
    $frames = @($pngs | ForEach-Object { , [IO.File]::ReadAllBytes($_) })
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $img = [System.Drawing.Image]::FromFile($pngs[$i]); $size = $img.Width; $img.Dispose()
        $b = if ($size -ge 256) { 0 } else { $size }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $bw.Write($f) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($out, $ms.ToArray())
    $bw.Dispose()
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-brand-out-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
New-Item -ItemType Directory -Force (Join-Path $public 'brand') | Out-Null
try {
    $markUrl = ([Uri](Join-Path $brand 'mark.svg')).AbsoluteUri
    $markBig = Join-Path $tmp 'mark-512.png'
    Render $markUrl $markBig 64 64
    # Render the SVG large by wrapping it, so small sizes are downscaled from a sharp source.
    $wrap = Join-Path $tmp 'mark.html'
    Set-Content -Path $wrap -Encoding UTF8 -Value ('<!doctype html><html><body style="margin:0;background:transparent"><img src="' + $markUrl + '" width="512" height="512" style="display:block"></body></html>')
    Render ([Uri]$wrap).AbsoluteUri $markBig 512 512
    foreach ($s in @(16, 20, 24, 32, 48, 64, 192, 256)) { Resize $markBig (Join-Path $tmp "mark-$s.png") $s }
    Copy-Item (Join-Path $tmp 'mark-256.png') (Join-Path $brand 'mark-256.png') -Force
    Copy-Item (Join-Path $tmp 'mark-64.png') (Join-Path $brand 'mark-64.png') -Force
    foreach ($s in @(32, 192, 256)) { Copy-Item (Join-Path $tmp "mark-$s.png") (Join-Path $public "brand\icon-$s.png") -Force }
    Write-Ico (Join-Path $brand 'favicon.ico') @((Join-Path $tmp 'mark-16.png'), (Join-Path $tmp 'mark-32.png'), (Join-Path $tmp 'mark-48.png'), (Join-Path $tmp 'mark-256.png'))
    Write-Ico (Join-Path $brand 'tray.ico') @((Join-Path $tmp 'mark-16.png'), (Join-Path $tmp 'mark-20.png'), (Join-Path $tmp 'mark-24.png'), (Join-Path $tmp 'mark-32.png'))
    Copy-Item (Join-Path $brand 'favicon.ico') (Join-Path $public 'favicon.ico') -Force
    if (-not $MarkOnly) {
        foreach ($theme in @('light', 'dark')) {
            $raw = Join-Path $tmp "emblem-$theme-raw.png"
            Render (([Uri](Join-Path $brand 'emblem-source.html')).AbsoluteUri + "?theme=$theme") $raw 512 512 2
            Resize $raw (Join-Path $brand "emblem-$theme.png") 1024
            Copy-Item (Join-Path $brand "emblem-$theme.png") (Join-Path $public "brand\emblem-$theme.png") -Force
        }
    }
    Write-Host ('Brand assets written to ' + $brand + ' and ' + $public) -ForegroundColor Green
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
