#requires -Version 5.1
# Pester 5 tests for the PowerShell layer and the repository rules. Run: Invoke-Pester tests/pester
# The core is compiled from src\Fairlead.Core\*.cs in memory by Import-FairleadCore (Add-Type), as on the workstation.

BeforeAll {
    $script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $script:Root 'module\Fairlead\Fairlead.psd1') -Force
    $script:Saved = @{}
    foreach ($n in @('FAIRLEAD_PORT', 'FAIRLEAD_BIND', 'FAIRLEAD_AUTO_STOP', 'FAIRLEAD_DATA_DIR', 'FAIRLEAD_LOG_DIR')) {
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
        foreach ($n in @('Start-Fairlead', 'Get-FairleadStatus', 'Import-FairleadCore', 'Initialize-FairleadEnvironment', 'Test-FairleadModelConfig')) { $names | Should -Contain $n }
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
        foreach ($n in @('FAIRLEAD_PORT', 'FAIRLEAD_BIND', 'FAIRLEAD_AUTO_STOP', 'FAIRLEAD_DATA_DIR')) { [Environment]::SetEnvironmentVariable($n, $null, 'Process') }
    }
    AfterEach { Remove-Item $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue }

    It 'defaults to the registry port, loopback, no auto-stop and %LOCALAPPDATA%\Fairlead' {
        $s = (Initialize-FairleadEnvironment -Root $script:Tmp).Settings
        $s.PreferredPort | Should -Be 8776
        $s.BindLan | Should -BeFalse
        $s.AutoStop | Should -BeFalse
        $s.DataDirectory | Should -Be (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Fairlead')
        $s.LogDirectory | Should -Be (Join-Path $script:Tmp 'logs')
    }

    It 'reads fairlead.env without overriding variables already set' {
        Set-Content (Join-Path $script:Tmp 'config\fairlead.env') "FAIRLEAD_PORT=9001`nFAIRLEAD_BIND=lan`nFAIRLEAD_AUTO_STOP=1"
        [Environment]::SetEnvironmentVariable('FAIRLEAD_PORT', '9002', 'Process')
        $s = (Initialize-FairleadEnvironment -Root $script:Tmp).Settings
        $s.PreferredPort | Should -Be 9002
        $s.BindLan | Should -BeTrue
        $s.AutoStop | Should -BeTrue
    }

    It 'rejects an unknown bind value' {
        Set-Content (Join-Path $script:Tmp 'config\fairlead.env') 'FAIRLEAD_BIND=everywhere'
        { Initialize-FairleadEnvironment -Root $script:Tmp } | Should -Throw '*loopback or lan*'
    }
}

Describe 'Test-FairleadModelConfig' {
    BeforeAll {
        $script:Gw = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-gw-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force (Join-Path $script:Gw 'config') | Out-Null
        $script:SavedGw = @{}
        foreach ($n in @('AI_GATEWAY_BASE_URL', 'DATABRICKS_TOKEN', 'FAIRLEAD_GATEWAY_CONFIG')) { $script:SavedGw[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
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
        foreach ($n in @('Open', 'NoBrowser', 'KeepConsole', 'NoTray', 'WithTab', 'Replace', 'Port')) { $p.Keys | Should -Contain $n }
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

Describe 'A real host on a free port' {
    It 'serves /v1/health, refuses /api without the session and writes then removes its runtime file' {
        Import-FairleadCore
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('fairlead-host-' + [guid]::NewGuid().ToString('N'))
        $settings = New-Object Fairlead.Core.Config.FairleadSettings
        $settings.RootDirectory = $tmp
        $settings.DataDirectory = (Join-Path $tmp 'data')
        $settings.LogDirectory = (Join-Path $tmp 'logs')
        $settings.PreferredPort = 0
        $hostObj = [Fairlead.Core.Facade]::CreateHost($settings, $null, 0, $null, $false)
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
