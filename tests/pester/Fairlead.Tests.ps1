#requires -Version 5.1
# Pester 5 tests for the PowerShell layer and the repository rules. Run: Invoke-Pester tests/pester
# The core is compiled from src\Fairlead.Core\*.cs in memory by Import-FairleadCore (Add-Type), as on the workstation.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Fairlead\Fairlead.psd1') -Force
    $script:Saved = @{}
    foreach ($n in @('FAIRLEAD_PORT', 'FAIRLEAD_BIND', 'FAIRLEAD_DATA_DIR', 'FAIRLEAD_LOG_DIR')) {
        $script:Saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
        [Environment]::SetEnvironmentVariable($n, $null, 'Process')
    }
}

AfterAll {
    foreach ($k in $script:Saved.Keys) { [Environment]::SetEnvironmentVariable($k, $script:Saved[$k], 'Process') }
    Remove-Module Fairlead -ErrorAction SilentlyContinue
}

Describe 'Module surface' {
    It 'exports the documented commands' {
        $names = (Get-Command -Module Fairlead).Name
        foreach ($n in @('Start-Fairlead', 'Get-FairleadStatus', 'Import-FairleadCore', 'Initialize-FairleadEnvironment', 'Test-FairleadModelConfig', 'Open-FairleadWindow', 'Send-FairleadOpenRequest')) { $names | Should -Contain $n }
    }
    It 'resolves the repository root' {
        Get-FairleadRoot | Should -Be $script:Root
    }
    It 'loads Fairlead.Core' {
        { Import-FairleadCore } | Should -Not -Throw
        ('Fairlead.Core.Facade' -as [type]) | Should -Not -BeNullOrEmpty
    }
    It 'compiles the core from source in memory and loads no DLL from disk' {
        Import-FairleadCore
        [string]::IsNullOrEmpty([Fairlead.Core.Facade].Assembly.Location) | Should -BeTrue -Because 'the workstation blocks loading custom DLL files'
        $fromRepo = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { -not $_.IsDynamic -and $_.Location -and $_.Location.StartsWith($script:Root, [StringComparison]::OrdinalIgnoreCase) })
        $fromRepo | Should -BeNullOrEmpty
        $refs = @([Fairlead.Core.Facade].Assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
        foreach ($r in $refs) { @('mscorlib', 'System', 'System.Core', 'System.Net.Http', 'System.Security', 'System.Security.Cryptography.ProtectedData', 'System.Runtime', 'netstandard') | Should -Contain $r }
        (Get-FairleadCoreLoadInfo) | Should -Not -BeNullOrEmpty
    }
    It 'lists the core source without build output folders' {
        $src = Get-FairleadCoreSource -Root $script:Root
        $src.Files.Count | Should -BeGreaterThan 20
        @($src.Files | Where-Object { $_ -match '[\\/](bin|obj)[\\/]' }) | Should -BeNullOrEmpty
    }
    It 'says what is missing when the core source is not there' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-nosrc-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $tmp | Out-Null
        try { { Get-FairleadCoreSource -Root $tmp } | Should -Throw '*core source folder*' }
        finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Describe 'Initialize-FairleadEnvironment' {
    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-pester-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:Tmp 'config') | Out-Null
        foreach ($n in @('FAIRLEAD_PORT', 'FAIRLEAD_BIND', 'FAIRLEAD_DATA_DIR')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
    }
    AfterEach { Remove-Item $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue }

    It 'defaults to the registry port, loopback and %LOCALAPPDATA%\Fairlead' {
        $s = (Initialize-FairleadEnvironment -Root $script:Tmp).Settings
        $s.PreferredPort | Should -Be 8776
        $s.DataDirectory | Should -Be (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Fairlead')
        $s.LogDirectory | Should -Be (Join-Path $script:Tmp 'logs')
    }

    It 'reads fairlead.env without overriding variables already set' {
        Set-Content (Join-Path $script:Tmp 'config\fairlead.env') "FAIRLEAD_PORT=9001`nFAIRLEAD_BIND=loopback"
        [Environment]::SetEnvironmentVariable('FAIRLEAD_PORT', '9002', 'Process')
        $s = (Initialize-FairleadEnvironment -Root $script:Tmp).Settings
        $s.PreferredPort | Should -Be 9002
    }

    It 'refuses any bind other than loopback, including the removed lan option' {
        foreach ($v in @('lan', 'everywhere')) {
            [Environment]::SetEnvironmentVariable('FAIRLEAD_BIND', $null, 'Process')
            Set-Content (Join-Path $script:Tmp 'config\fairlead.env') "FAIRLEAD_BIND=$v"
            { Initialize-FairleadEnvironment -Root $script:Tmp } | Should -Throw '*loopback*'
        }
    }
}

Describe 'Test-FairleadModelConfig' {
    BeforeAll {
        $script:Gw = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-gw-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:Gw 'config') | Out-Null
        $script:SavedGw = @{}
        # Earlier tests leave fairlead.env values (FAIRLEAD_BIND=everywhere) in the process; start clean.
        foreach ($n in @('AI_GATEWAY_BASE_URL', 'DATABRICKS_TOKEN', 'FAIRLEAD_GATEWAY_CONFIG', 'FAIRLEAD_PORT', 'FAIRLEAD_BIND', 'FAIRLEAD_AUTO_STOP', 'FAIRLEAD_DATA_DIR')) {
            $script:SavedGw[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
            [Environment]::SetEnvironmentVariable($n, $null, 'Process')
        }
    }
    AfterAll {
        foreach ($k in $script:SavedGw.Keys) { [Environment]::SetEnvironmentVariable($k, $script:SavedGw[$k], 'Process') }
        Remove-Item $script:Gw -Recurse -Force -ErrorAction SilentlyContinue
    }
    It 'accepts the committed example when its variables are set, and prints no value' {
        [Environment]::SetEnvironmentVariable('AI_GATEWAY_BASE_URL', 'https://approved-gateway.example', 'Process')
        [Environment]::SetEnvironmentVariable('DATABRICKS_TOKEN', 'made-up-token', 'Process')
        $example = Join-Path $script:Root 'config\ai_gateway_models.example.yaml'
        $out = Test-FairleadModelConfig -Path $example -Root $script:Gw 6>&1
        ($out | Where-Object { $_ -is [bool] }) | Should -Be $true
        ($out -join "`n") | Should -Not -Match 'approved-gateway|made-up-token|example\.provider-model'
    }
    It 'names a missing variable and fails' {
        [Environment]::SetEnvironmentVariable('DATABRICKS_TOKEN', $null, 'Process')
        $out = Test-FairleadModelConfig -Path (Join-Path $script:Root 'config\ai_gateway_models.example.yaml') -Root $script:Gw 6>&1
        ($out | Where-Object { $_ -is [bool] }) | Should -Be $false
        ($out -join "`n") | Should -Match 'DATABRICKS_TOKEN'
    }
    It 'returns false when there is no model file' {
        [Environment]::SetEnvironmentVariable('FAIRLEAD_GATEWAY_CONFIG', $null, 'Process')
        (Test-FairleadModelConfig -Root $script:Gw 6>$null) | Should -Be $false
    }
}

Describe 'Launcher' {
    It 'accepts the documented switches' {
        $p = (Get-Command (Join-Path $script:Root 'scripts\Start-Fairlead.ps1')).Parameters
        foreach ($n in @('Open', 'NoBrowser', 'KeepConsole', 'NoTray', 'Replace', 'Port')) { $p.Keys | Should -Contain $n }
        $p.Keys | Should -Not -Contain 'WithTab'   # no option stops Fairlead when the browser closes
    }
    It 'Launch-Fairlead.bat runs Start-Fairlead.ps1 in an STA PowerShell 5.1 (the tray needs STA)' {
        $bat = Get-Content (Join-Path $script:Root 'Launch-Fairlead.bat') -Raw
        $bat | Should -Match 'powershell\.exe -STA'
        $bat | Should -Match 'scripts\\Start-Fairlead\.ps1'
    }
    It 'Stop-Fairlead.ps1 reports nothing running in a fresh folder' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-stop-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $tmp 'logs') | Out-Null
        . (Join-Path $script:Root 'scripts\Fairlead-Process.ps1')
        @(Get-FairleadProcesses -Root $tmp | Where-Object { $_.Url }) | Should -HaveCount 0
        Remove-Item $tmp -Recurse -Force
    }
}

Describe 'Tray icon art' {
    It 'asks the FleetHacker tray kit for the Fairlead icon, with tray.ico as the fallback and a busy badge in Kedge mode' {
        InModuleScope Fairlead {
            Mock Get-FleetTrayIcon { $null }
            $ico = Join-Path (Get-FairleadRoot) 'assets\brand\tray.ico'
            [void](Get-FairleadTrayIcon -IconPath $ico)
            Should -Invoke Get-FleetTrayIcon -Times 1 -Exactly -ParameterFilter { $App -eq 'fairlead' -and $Status -eq '' -and $FallbackPath -eq $ico }
            [void](Get-FairleadTrayIcon -IconPath $ico -Kedge $true)
            Should -Invoke Get-FleetTrayIcon -Times 1 -Exactly -ParameterFilter { $App -eq 'fairlead' -and $Status -eq 'busy' -and $FallbackPath -eq $ico }
            $body = (Get-Command Invoke-FairleadTray).Definition
            $body | Should -Match 'Get-FairleadTrayIcon -IconPath \$IconPath'
            $body | Should -Not -Match 'System\.Drawing\.Icon\(\$IconPath\)'
        }
    }
    It 'draws the icon at the tray size, and tray.ico carries the tray sizes for sites without Add-Type' {
        InModuleScope Fairlead {
            $ico = Join-Path (Get-FairleadRoot) 'assets\brand\tray.ico'
            $i = Get-FairleadTrayIcon -IconPath $ico -Kedge $true
            try { $i.Width | Should -Be (Get-FleetTraySize) } finally { $i.Dispose() }
            foreach ($px in @(16, 20, 24, 32)) {
                $f = New-Object System.Drawing.Icon($ico, $px, $px)
                try { $f.Width | Should -Be $px } finally { $f.Dispose() }
            }
        }
    }
    It 'keeps the kit private to the module and records its edge-ops version' {
        (Get-Command -Module Fairlead).Name | Should -Not -Contain 'Get-FleetTrayIcon'
        Get-Content (Join-Path $script:Root 'module\Fairlead\vendor\fleet-tray\README.md') -Raw | Should -Match 'ui-kit/tray v1\.0\.0, commit 2f90369'
    }
}

Describe 'App window and single instance' {
    It 'finds the first browser that exists and none when none does' {
        $fake = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-browser-' + [guid]::NewGuid().ToString('N') + '.exe')
        Set-Content -LiteralPath $fake -Value 'x'
        try {
            Find-FairleadBrowser -Candidates @((Join-Path ([IO.Path]::GetTempPath()) 'no-such-browser.exe'), $fake) | Should -Be $fake
            Find-FairleadBrowser -Candidates @((Join-Path ([IO.Path]::GetTempPath()) 'no-such-browser.exe')) | Should -BeNullOrEmpty
        } finally { Remove-Item -LiteralPath $fake -Force -ErrorAction SilentlyContinue }
    }
    It 'builds an --app= command line with its own profile and the background features off' {
        $a = Get-FairleadBrowserArguments -Url 'http://127.0.0.1:8776/?session=abc' -ProfileDir 'C:\Data Dir\app-window'
        $a | Should -Contain '--app=http://127.0.0.1:8776/?session=abc'
        $a | Should -Contain '--user-data-dir="C:\Data Dir\app-window"'
        foreach ($f in @('--disable-extensions', '--disable-sync', '--disable-background-networking', '--disable-component-update', '--no-first-run')) { $a | Should -Contain $f }
    }
    It 'a second launch signals the running instance once, and reports false when nobody is listening' {
        $name = 'Local\Fairlead.Test.' + [guid]::NewGuid().ToString('N')
        Send-FairleadOpenRequest -Name $name | Should -BeFalse
        $listener = New-FairleadOpenListener -Name $name
        try {
            Test-FairleadOpenRequested -Listener $listener | Should -BeFalse
            Send-FairleadOpenRequest -Name $name | Should -BeTrue
            Test-FairleadOpenRequested -Listener $listener | Should -BeTrue
            Test-FairleadOpenRequested -Listener $listener | Should -BeFalse
        } finally { $listener.Dispose() }
        Send-FairleadOpenRequest -Name $name | Should -BeFalse
    }
    It 'the Quit confirmation says tools that use Fairlead will stop working' {
        Get-FairleadQuitPrompt | Should -Match 'Tools that use Fairlead will stop working'
    }
}

Describe 'Lifecycle without a browser' {
    BeforeEach {
        Import-FairleadCore
        $script:LcTmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-life-' + [guid]::NewGuid().ToString('N'))
        $settings = New-Object Fairlead.Core.Config.FairleadSettings
        $settings.RootDirectory = $script:LcTmp
        $settings.DataDirectory = (Join-Path $script:LcTmp 'data')
        $settings.LogDirectory = (Join-Path $script:LcTmp 'logs')
        $settings.PreferredPort = 0
        $script:LcHost = [Fairlead.Core.Facade]::CreateHost($settings, $null, 0, $null)
        $script:LcInfo = $script:LcHost.Start()
    }
    AfterEach {
        $script:LcHost.Dispose()
        Remove-Item $script:LcTmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    It 'keeps serving after the last browser tab says it is leaving' {
        $h = @{ 'X-Fairlead-Session' = $script:LcInfo.SessionToken }
        for ($i = 0; $i -lt 2; $i++) {
            (Invoke-RestMethod -Uri ($script:LcInfo.Url + 'api/heartbeat') -Method Post -Headers $h -Body '{"leaving":true}' -ContentType 'application/json' -TimeoutSec 5).ok | Should -BeTrue
        }
        Start-Sleep -Seconds 3
        $script:LcHost.StopRequested | Should -BeFalse
        $script:LcHost.WaitForStop(100) | Should -BeFalse
        (Invoke-RestMethod -Uri ($script:LcInfo.GatewayUrl + '/health') -TimeoutSec 5).ok | Should -BeTrue
    }
    It 'Quit (Stop) ends the host: the wait returns, the port closes and the runtime file is gone' {
        Test-Path $script:LcInfo.RuntimeFile | Should -BeTrue
        $script:LcHost.Stop()
        $script:LcHost.StopRequested | Should -BeTrue
        $script:LcHost.WaitForStop(2000) | Should -BeTrue
        { Invoke-RestMethod -Uri ($script:LcInfo.GatewayUrl + '/health') -TimeoutSec 3 } | Should -Throw
        Test-Path $script:LcInfo.RuntimeFile | Should -BeFalse
    }
    It 'answers /__fleethacker/id as fairlead, and nothing answers once it has stopped' {
        (Get-FairleadInstanceId -GatewayUrl $script:LcInfo.GatewayUrl).tool | Should -Be 'fairlead'
        # No script given: the id names this process's executable, exactly as Win32_Process reports it.
        $id = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $script:LcInfo.Port + '/__fleethacker/id') -TimeoutSec 5
        $id.pid | Should -Be $PID
        $exe = (Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $PID)).ExecutablePath
        $id.path | Should -Be $exe
        $script:LcHost.Stop()
        Get-FairleadInstanceId -GatewayUrl $script:LcInfo.GatewayUrl | Should -BeNullOrEmpty
    }
    It 'the tray tooltip names Fairlead and its status within the 63-character limit, and the menu line gives the address' {
        $t = [Fairlead.Core.Facade]::TrayText($script:LcHost)
        $t.Length | Should -BeLessOrEqual 63
        $t | Should -Match '^Fairlead - running on 127\.0\.0\.1:\d+$'
        [Fairlead.Core.Facade]::TrayStatus($script:LcHost) | Should -Match '^Running on 127\.0\.0\.1:\d+ \('
    }
}

Describe 'The FleetPort id path' {
    It 'reports the script Start-Fairlead.ps1 passes, absolute and normalized' {
        Import-FairleadCore
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-idpath-' + [guid]::NewGuid().ToString('N'))
        $settings = New-Object Fairlead.Core.Config.FairleadSettings
        $settings.RootDirectory = $tmp
        $settings.DataDirectory = (Join-Path $tmp 'data')
        $settings.LogDirectory = (Join-Path $tmp 'logs')
        $settings.PreferredPort = 0
        $startScript = Join-Path $PSScriptRoot '..\..\scripts\Start-Fairlead.ps1'
        $hostObj = [Fairlead.Core.Facade]::CreateHost($settings, $null, 0, $null, $startScript)
        try {
            $info = $hostObj.Start()
            $id = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $info.Port + '/__fleethacker/id') -TimeoutSec 5
            $id.tool | Should -Be 'fairlead'
            $id.path | Should -Be ([IO.Path]::GetFullPath($startScript))
            $id.path | Should -Not -Match '\\\.\.\\'
            Test-Path -LiteralPath $id.path | Should -BeTrue
        } finally {
            $hostObj.Dispose()
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    It 'Start-Fairlead.ps1 hands its own path to the host' {
        $text = Get-Content (Join-Path $PSScriptRoot '..\..\scripts\Start-Fairlead.ps1') -Raw
        $text | Should -Match 'ScriptPath = \$PSCommandPath'
    }
}

Describe 'A real host on a free port' {
    It 'serves /v1/health, refuses /api without the session and writes then removes its runtime file' {
        Import-FairleadCore
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-host-' + [guid]::NewGuid().ToString('N'))
        $settings = New-Object Fairlead.Core.Config.FairleadSettings
        $settings.RootDirectory = $tmp
        $settings.DataDirectory = (Join-Path $tmp 'data')
        $settings.LogDirectory = (Join-Path $tmp 'logs')
        $settings.PreferredPort = 0
        $hostObj = [Fairlead.Core.Facade]::CreateHost($settings, $null, 0, $null)
        try {
            $info = $hostObj.Start()
            Test-Path $info.RuntimeFile | Should -BeTrue
            (Get-Content $info.RuntimeFile -Raw) | Should -Not -Match $info.SessionToken
            $h = Invoke-RestMethod -Uri ($info.GatewayUrl + '/health') -TimeoutSec 5
            $h.ok | Should -BeTrue
            { Invoke-RestMethod -Uri ($info.Url + 'api/config') -TimeoutSec 5 } | Should -Throw
            $cfg = Invoke-RestMethod -Uri ($info.Url + 'api/config') -Headers @{ 'X-Fairlead-Session' = $info.SessionToken } -TimeoutSec 5
            $cfg.routes.id | Should -Contain 'local'
            $runtime = $info.RuntimeFile
        } finally {
            $hostObj.Dispose()
        }
        Test-Path $runtime | Should -BeFalse
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'The fairlead CLI against a real host' {
    BeforeAll {
        Import-FairleadCore
        $script:CliTmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-cli-' + [guid]::NewGuid().ToString('N'))
        $settings = New-Object Fairlead.Core.Config.FairleadSettings
        $settings.RootDirectory = $script:CliTmp
        $settings.DataDirectory = (Join-Path $script:CliTmp 'data')
        $settings.LogDirectory = (Join-Path $script:CliTmp 'logs')
        $settings.PreferredPort = 0
        $script:CliHost = [Fairlead.Core.Facade]::CreateHost($settings, $null, 0, $null)
        $script:CliInfo = $script:CliHost.Start()
        $env:FAIRLEAD_DATA_DIR = $settings.DataDirectory
        $script:Cli = Join-Path $script:Root 'fairlead.cmd'
        function script:Invoke-Cli {
            # Windows PowerShell wraps native stderr as ErrorRecords. Capture expected
            # CLI failures instead of letting Pester's Stop preference terminate early.
            $ErrorActionPreference = 'Continue'
            $out = & $script:Cli @args 2>&1 | Out-String
            return @{ Out = $out; Code = $LASTEXITCODE }
        }
        function script:Invoke-CliJson { $r = Invoke-Cli @args --json; $r.Code | Should -Be 0 -Because $r.Out; return ($r.Out | ConvertFrom-Json) }
    }
    AfterAll {
        if ($script:CliHost) { $script:CliHost.Dispose() }
        $env:FAIRLEAD_DATA_DIR = $null
        Remove-Item $script:CliTmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'finds the engine through its DPAPI-protected access file, which holds no plain token' {
        $file = Join-Path $script:CliTmp 'data\fairlead-cli.json'
        Test-Path $file | Should -BeTrue
        (Get-Content $file -Raw) | Should -Not -Match $script:CliInfo.SessionToken
        $r = Invoke-Cli status
        $r.Code | Should -Be 0
        $r.Out | Should -Match 'Fairlead 0\.'
        (Invoke-CliJson api).routes | Should -Contain 'POST /api/routes'
    }

    It 'refuses remote or ambiguous admin URLs before reading or sending a session token' {
        foreach ($url in @('https://example.invalid', 'http://127.0.0.1@example.invalid', 'http://localhost/api', 'http://localhost/?x=1', 'http://localhost/#x', 'file:///C:/temp')) {
            $r = Invoke-Cli status --url $url
            $r.Code | Should -Be 1
            $r.Out | Should -Match 'loopback'
            $r.Out | Should -Not -Match $script:CliInfo.SessionToken
        }
    }

    It 'refuses a command-line session token and accepts the environment path for a loopback engine' {
        $r = Invoke-Cli status --session synthetic-session
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'FAIRLEAD_SESSION'
        $r.Out | Should -Not -Match 'synthetic-session'
        $old = $env:FAIRLEAD_SESSION
        try {
            $env:FAIRLEAD_SESSION = $script:CliHost.CliToken
            $r = Invoke-Cli status --url $script:CliInfo.Url
            $r.Code | Should -Be 0 -Because $r.Out
        } finally { $env:FAIRLEAD_SESSION = $old }
    }

    It 'changes routes, providers, clients, settings and Kedge mode through the API' {
        (Invoke-Cli routes set smart --targets 'ollama/qwen,ollama/tiny' --strategy fastest).Code | Should -Be 0
        $smart = Invoke-CliJson routes show smart
        @($smart.targets) | Should -Be @('ollama/qwen', 'ollama/tiny')
        $smart.strategy | Should -Be 'fastest'
        (Invoke-Cli routes set smart --remove-target ollama/tiny).Code | Should -Be 0
        @((Invoke-CliJson routes show smart).targets) | Should -Be @('ollama/qwen')

        (Invoke-Cli providers set groq --preset groq --enabled false).Code | Should -Be 0
        (Invoke-CliJson providers show groq).enabled | Should -BeFalse
        (Invoke-Cli providers enable groq).Code | Should -Be 0
        (Invoke-CliJson providers show groq).enabled | Should -BeTrue

        $added = Invoke-Cli clients add ci-bot --routes local
        $added.Out | Should -Match 'fl-[A-Za-z0-9]{20,}'
        (Invoke-CliJson clients list).name | Should -Contain 'ci-bot'
        (Invoke-Cli clients remove ci-bot --yes).Code | Should -Be 0
        @((Invoke-CliJson clients list)).Count | Should -Be 0

        (Invoke-Cli settings set requireClientKey=false privacy.detectSecrets=false 'corsOrigins=http://localhost:3000').Code | Should -Be 0
        $s = Invoke-CliJson settings show
        $s.requireClientKey | Should -BeFalse
        $s.privacy.detectSecrets | Should -BeFalse
        $s.privacy.forceLocalWhenSensitive | Should -BeTrue
        @($s.corsOrigins) | Should -Be @('http://localhost:3000')

        (Invoke-Cli kedge on).Out | Should -Match 'Kedge mode on'
        (Invoke-CliJson config).kedge | Should -Be 'on'
        (Invoke-Cli explain 'hello there').Out | Should -Match 'Route: '

        $listed = Invoke-Cli clients install
        $listed.Code | Should -Be 0
        $listed.Out | Should -Match 'PacketRat'
        $listed.Out | Should -Match 'Gangplank'
        $listed.Out | Should -Not -Match 'fl-'
    }

    It 'runs every read-only command, in tables and as JSON' {
        foreach ($c in @(@('help'), @('config'), @('presets'), @('providers'), @('routes'), @('routing'), @('clients'), @('clients', 'usage'), @('settings'), @('kedge'), @('gateway'), @('lazarette'), @('health'), @('breakers'), @('cache'), @('requests'), @('requestlog'), @('requestlog', '--summary'), @('requestlog', 'prune', '--days', '7', '--dry-run'), @('bearing'), @('commands'), @('metrics'), @('logs', '--level', 'info'))) {
            $r = Invoke-Cli @c
            $r.Code | Should -Be 0 -Because (($c -join ' ') + ': ' + $r.Out)
            if ($c[0] -ne 'help') { { Invoke-CliJson @c } | Should -Not -Throw -Because ($c -join ' ') }
        }
    }

    It 'lists every command as JSON with a versioned schema, offline' {
        $doc = Invoke-CliJson commands
        $doc.schemaVersion | Should -Be 1
        $doc.tool | Should -Be 'fairlead'
        $names = @($doc.commands | ForEach-Object { $_.name })
        foreach ($n in @('providers', 'routes', 'clients', 'requests', 'requestlog', 'kedge', 'stop', 'commands', 'api')) { $names | Should -Contain $n }
        ($names | Select-Object -Unique).Count | Should -BeGreaterThan 20
        $script = Get-Content -Raw (Join-Path $script:Root 'scripts/fairlead.ps1')
        foreach ($c in $doc.commands) { $script | Should -Match ("'" + [regex]::Escape($c.name) + "'") -Because ('command ' + $c.name + ' is listed but not handled') }
        ($doc.commands | Where-Object { $_.name -eq 'stop' }).destructive | Should -BeTrue
        ($doc.commands | Where-Object { $_.name -eq 'status' }).destructive | Should -BeFalse
        @(($doc.commands | Where-Object { $_.name -eq 'status' }).routes) | Should -Contain 'GET /api/status'
        (Invoke-Cli commands).Out | Should -Match 'requestlog'
    }

    It 'takes a provider key from stdin, never from the command line, and never prints it back' {
        $out = 'sk-test-0123456789abcdef' | & $script:Cli providers key groq 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0 -Because $out
        $p = Invoke-CliJson providers show groq
        $p.keySource | Should -Be 'stored'
        ($p | ConvertTo-Json -Depth 5) | Should -Not -Match '0123456789abcdef'
    }

    It 'fails with a message and exit code 1, and refuses destructive commands from a script without --yes' {
        $r = Invoke-Cli providers show nope
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'No provider nope'
        $r = & {
            $ErrorActionPreference = 'Continue'
            $out = '' | & $script:Cli routes remove fast 2>&1 | Out-String
            return @{ Out = $out; Code = $LASTEXITCODE }
        }
        $r.Code | Should -Be 1
        $r.Out | Should -Match '--yes'
        (Invoke-CliJson routes show fast).id | Should -Be 'fast'
    }

    It 'says Fairlead is not running once the engine stops and its access file is gone' {
        $script:CliHost.Dispose(); $script:CliHost = $null
        Test-Path (Join-Path $script:CliTmp 'data\fairlead-cli.json') | Should -BeFalse
        $r = Invoke-Cli status
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'not running'
    }
}

Describe 'Client-key registry' {
    BeforeAll {
        $script:RegistryEap = $ErrorActionPreference
        $script:RegistryRoot = $script:Root
        . (Join-Path $script:Root 'scripts\fairlead.ps1')
        $ErrorActionPreference = $script:RegistryEap
        $script:Root = $script:RegistryRoot
    }

    It 'lists Gangplank the same way as PacketRat, in step with the Clients page' {
        $apps = @(Get-ClientKeyRegistry)
        $apps | Should -Contain 'PacketRat'
        $apps | Should -Contain 'Gangplank'
        ($apps | Select-Object -Unique).Count | Should -Be $apps.Count
        foreach ($app in $apps) { Test-ClientAppName $app | Should -BeTrue }
        Test-ClientAppName '9app' | Should -BeFalse
        Test-ClientAppName '../x' | Should -BeFalse
        $web = Get-Content (Join-Path $script:Root 'web\src\lib\apps.ts') -Raw
        foreach ($app in $apps) { $web | Should -Match ("'" + $app + "'") }
        (Get-HelpText) | Should -Match 'clients install \[<app>\]'
        # Get-CommandList returns one array, so the first element of @() is that array.
        $commands = @(Get-CommandList)[0]
        $install = @($commands | Where-Object { $_.usage -like 'clients install*' })
        $install.Count | Should -Be 1
        $install[0].name | Should -Be 'clients'
        @($install[0].routes) | Should -Contain 'POST /api/clients'
        $install[0].destructive | Should -BeFalse
    }

    It 'refuses to print a key that an install answer included' {
        $ok = New-Object psobject -Property @{ installed = 'Fairlead/Gangplank' }
        { Assert-InstalledClientKey $ok } | Should -Not -Throw
        $bad = New-Object psobject -Property @{ installed = 'Fairlead/Gangplank'; key = 'fl-example' }
        { Assert-InstalledClientKey $bad } | Should -Throw '*refusing to print*'
    }
}

Describe 'Repository rules' {
    BeforeAll {
        $script:Ps = @(Get-ChildItem -Path $script:Root -Recurse -Include *.ps1, *.psm1, *.psd1 -File | Where-Object { $_.FullName -notmatch '\\(node_modules|dist|bin|obj)\\' })
    }
    It 'PowerShell files are ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI)' {
        foreach ($f in $script:Ps) {
            $bytes = [IO.File]::ReadAllBytes($f.FullName)
            ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0 -Because $f.FullName
        }
    }
    It 'PowerShell files use no PowerShell 7-only operators' {
        foreach ($f in $script:Ps) {
            $tokens = $null; $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
            $errors | Should -BeNullOrEmpty -Because $f.FullName
            ($tokens | Where-Object { $_.Kind -in @('QuestionQuestion', 'QuestionQuestionEquals', 'AndAnd', 'OrOr', 'QuestionDot', 'QuestionLBracket') }) | Should -BeNullOrEmpty -Because $f.FullName
        }
    }
    It 'the core references no third-party package and the repository holds no DLL' {
        $proj = Get-Content (Join-Path $script:Root 'src\Fairlead.Core\Fairlead.Core.csproj') -Raw
        ([regex]::Matches($proj, '<PackageReference Include="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) | Where-Object { $_ -ne 'Microsoft.NETFramework.ReferenceAssemblies' } | Should -BeNullOrEmpty
        $proj | Should -Match '<LangVersion>5</LangVersion>'
        @(Get-ChildItem -Path $script:Root -Recurse -File -Include *.dll, *.exe | Where-Object { $_.FullName -notmatch '\\(node_modules|dist|bin|obj|\.git)\\' }) | Should -BeNullOrEmpty
        # Code lines only: comments may name Newtonsoft (the JSON model keeps its output identical to it).
        $hits = foreach ($f in @(Get-ChildItem -Path (Join-Path $script:Root 'src\Fairlead.Core') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
            Select-String -LiteralPath $f.FullName -Pattern '\bNewtonsoft\b|Assembly\.Load' | Where-Object { $_.Line.TrimStart() -notmatch '^//' } | ForEach-Object { $f.Name + ':' + $_.LineNumber }
        }
        @($hits) | Should -BeNullOrEmpty
    }
    It 'core C# files are ASCII (the .NET Framework compiler behind Add-Type may read BOM-less files as ANSI)' {
        foreach ($f in @(Get-ChildItem -Path (Join-Path $script:Root 'src\Fairlead.Core') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
            $bytes = [IO.File]::ReadAllBytes($f.FullName)
            ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0 -Because $f.FullName
        }
    }
    It 'only Providers\UpstreamClient.cs makes outbound HTTP calls' {
        $offenders = Get-ChildItem -Path (Join-Path $script:Root 'src\Fairlead.Core') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'UpstreamClient.cs' } |
            Where-Object { (Get-Content $_.FullName -Raw) -match 'new HttpClient\(|\.SendAsync\(new HttpRequestMessage|WebRequest\.Create' }
        $offenders | Should -BeNullOrEmpty
    }
    It 'no real keys or session tokens are committed' {
        $files = Get-ChildItem -Path $script:Root -Recurse -File -Include *.cs, *.ts, *.tsx, *.ps1, *.psm1, *.md, *.json, *.env*, *.yml | Where-Object { $_.FullName -notmatch '\\(node_modules|dist|bin|obj|\.git)\\' -and $_.Name -ne 'package-lock.json' }
        foreach ($f in $files) {
            $t = Get-Content $f.FullName -Raw
            $t | Should -Not -Match 'sk-ant-api\d\d-[A-Za-z0-9_\-]{20,}' -Because $f.FullName
            $t | Should -Not -Match 'sk-proj-[A-Za-z0-9_\-]{40,}' -Because $f.FullName
        }
    }
}
