#requires -Version 5.1
# The tray icon: Fairlead's way out and its at-a-glance state (TechStack: tray apps get Quit in the tray menu).
# Runs a WinForms message loop on the launcher's STA thread until Quit, End session or Stop-Fairlead.
# Closing the browser never stops Fairlead: other tools use its one local address. Menu: status line (disabled),
# Open Fairlead (also double-click), Copy gateway URL, Kedge mode, Quit Fairlead (asks first).

function Test-FairleadTraySupported {
    if ($env:OS -ne 'Windows_NT') { return $false }
    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { return $false }
    try { Add-Type -AssemblyName System.Windows.Forms, System.Drawing -ErrorAction Stop; return $true } catch { return $false }
}

function Get-FairleadQuitPrompt {
    <# The one-line confirmation shown before the router stops. #>
    return 'Quit Fairlead? Tools that use Fairlead will stop working.'
}

function Invoke-FairleadTray {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$HostObject,
        [Parameter(Mandatory = $true)][string]$LaunchUrl,
        [Parameter(Mandatory = $true)][string]$GatewayUrl,
        [string]$IconPath,
        [string]$ProfileDir
    )
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $icon = New-Object System.Windows.Forms.NotifyIcon
    if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { $icon.Icon = New-Object System.Drawing.Icon($IconPath) }
    else { $icon.Icon = [System.Drawing.SystemIcons]::Application }
    $icon.Text = [Fairlead.Core.Facade]::TrayText($HostObject)
    $icon.Visible = $true

    # A second launch signals this event; the timer below reopens the window. Nothing here needs Lazarette or Windlass.
    $listener = $null
    try { $listener = New-FairleadOpenListener } catch { Write-Warning ('Single-instance signal unavailable: ' + $_.Exception.Message) }

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $menu.AccessibleName = 'Fairlead'
    $state = $menu.Items.Add([Fairlead.Core.Facade]::TrayStatus($HostObject))
    $state.Enabled = $false
    $state.AccessibleName = 'Fairlead status'
    [void]$menu.Items.Add('-')
    $openItem = $menu.Items.Add('&Open Fairlead')
    $openItem.Font = New-Object System.Drawing.Font($openItem.Font, [System.Drawing.FontStyle]::Bold)
    $openItem.add_Click({ [void](Open-FairleadWindow -Url $LaunchUrl -ProfileDir $ProfileDir) })
    $copyItem = $menu.Items.Add('&Copy gateway URL')
    $copyItem.add_Click({ [System.Windows.Forms.Clipboard]::SetText($GatewayUrl); $icon.ShowBalloonTip(2000, 'Fairlead', ('Copied ' + $GatewayUrl), [System.Windows.Forms.ToolTipIcon]::Info) })
    $kedgeMenu = New-Object System.Windows.Forms.ToolStripMenuItem('&Kedge mode')
    $kAuto = $kedgeMenu.DropDownItems.Add('&Auto (local when offline)')
    $kOn = $kedgeMenu.DropDownItems.Add('O&n (local models only)')
    $kOff = $kedgeMenu.DropDownItems.Add('O&ff (never switch automatically)')
    $kAuto.add_Click({ [Fairlead.Core.Facade]::SetKedge($HostObject, 'auto') })
    $kOn.add_Click({ [Fairlead.Core.Facade]::SetKedge($HostObject, 'on') })
    $kOff.add_Click({ [Fairlead.Core.Facade]::SetKedge($HostObject, 'off') })
    [void]$menu.Items.Add($kedgeMenu)
    [void]$menu.Items.Add('-')
    $quitItem = $menu.Items.Add('&Quit Fairlead')
    $quitItem.add_Click({
        $answer = [System.Windows.Forms.MessageBox]::Show((Get-FairleadQuitPrompt), 'Quit Fairlead',
            [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) { $HostObject.Stop() }
    })
    $icon.ContextMenuStrip = $menu
    $icon.add_DoubleClick({ [void](Open-FairleadWindow -Url $LaunchUrl -ProfileDir $ProfileDir) })

    $script:FairleadLastKedge = $HostObject.Health.KedgeActive
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 1500
    $timer.add_Tick({
        if ($HostObject.StopRequested) {
            $timer.Stop()
            [System.Windows.Forms.Application]::ExitThread()
            return
        }
        if ($listener -and (Test-FairleadOpenRequested -Listener $listener)) { [void](Open-FairleadWindow -Url $LaunchUrl -ProfileDir $ProfileDir) }
        $icon.Text = [Fairlead.Core.Facade]::TrayText($HostObject)
        $state.Text = [Fairlead.Core.Facade]::TrayStatus($HostObject)
        $mode = $HostObject.Config.Snapshot().Kedge.ToString()
        $kAuto.Checked = ($mode -eq 'Auto'); $kOn.Checked = ($mode -eq 'On'); $kOff.Checked = ($mode -eq 'Off')
        $k = $HostObject.Health.KedgeActive
        if ($k -ne $script:FairleadLastKedge) {
            $script:FairleadLastKedge = $k
            if ($k) { $icon.ShowBalloonTip(4000, 'Fairlead: Kedge mode', 'Cloud unreachable or forced off. Requests now go to local models.', [System.Windows.Forms.ToolTipIcon]::Warning) }
            else { $icon.ShowBalloonTip(3000, 'Fairlead', 'Cloud routes are back.', [System.Windows.Forms.ToolTipIcon]::Info) }
        }
    })
    $timer.Start()
    try {
        [System.Windows.Forms.Application]::Run()
    } finally {
        $timer.Dispose()
        if ($listener) { $listener.Dispose() }
        $icon.Visible = $false
        $icon.Dispose()
        $menu.Dispose()
    }
}
