<#
.SYNOPSIS
    fairlead: the command line for every Fairlead capability.
.DESCRIPTION
    Each command calls the running engine's admin API (docs\API.md), the same API the browser UI uses, so anything
    the UI can do, a script can do. The engine writes a CLI token for this Windows user only (DPAPI) to
    %LOCALAPPDATA%\Fairlead\fairlead-cli.json while it runs; this script reads it. Secrets (provider keys, gateway
    values) are read from stdin or a hidden prompt, never from arguments, so they stay out of shell history.

    Run "fairlead help" for the command list. Windows PowerShell 5.1; ASCII only.
#>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Argv)

$ErrorActionPreference = 'Stop'
$script:Root = Split-Path $PSScriptRoot -Parent
$script:Flags = @('json', 'yes', 'private', 'tools', 'clear-key', 'key-stdin', 'console', 'no-tray', 'no-browser', 'open', 'replace', 'remove', 'summary')

# ---------- arguments ----------

function Split-Arguments([string[]]$List) {
    $words = New-Object System.Collections.Generic.List[string]
    $opts = @{}
    $i = 0
    while ($i -lt $List.Count) {
        $a = $List[$i]
        if ($a -like '--*' -and $a.Length -gt 2) {
            $name = $a.Substring(2)
            $eq = $name.IndexOf('=')
            if ($eq -gt 0) { $opts[$name.Substring(0, $eq)] = $name.Substring($eq + 1) }
            elseif ($script:Flags -contains $name) { $opts[$name] = $true }
            elseif ($i + 1 -lt $List.Count) { $opts[$name] = $List[$i + 1]; $i++ }
            else { throw "Option --$name needs a value." }
        } else { $words.Add($a) }
        $i++
    }
    return @{ Words = @($words); Opts = $opts }
}

function Opt([string]$Name, $Default = $null) {
    if ($script:Cli.Opts.ContainsKey($Name)) { return $script:Cli.Opts[$Name] }
    return $Default
}

function Word([int]$Index, [string]$What) {
    $w = $script:Cli.Words
    if ($w.Count -gt $Index) { return $w[$Index] }
    if ($What) { throw "Missing $What. Run: fairlead help" }
    return $null
}

function Rest([int]$From) {
    $w = $script:Cli.Words
    if ($w.Count -le $From) { return @() }
    return @($w[$From..($w.Count - 1)])
}

function ConvertTo-Bool([string]$Value, [string]$Name) {
    switch -Regex ($Value) {
        '^(true|yes|on|1)$' { return $true }
        '^(false|no|off|0)$' { return $false }
        default { throw "--$Name takes true or false, not '$Value'." }
    }
}

function Split-List([string]$Value) {
    if (-not $Value) { return @() }
    return @($Value.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# ---------- engine connection ----------

function Assert-LocalEngineUrl([string]$Value) {
    $parsed = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -notin @('http', 'https') -or $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment -or
        $parsed.AbsolutePath.Trim('/') -ne '') { throw 'The Fairlead admin URL must be a loopback HTTP(S) origin with no path, credentials, query or fragment.' }
    $ip = $null
    $local = $parsed.DnsSafeHost -ieq 'localhost'
    if ([Net.IPAddress]::TryParse($parsed.DnsSafeHost.Trim('[', ']'), [ref]$ip)) { $local = [Net.IPAddress]::IsLoopback($ip) }
    if (-not $local) { throw 'The Fairlead admin URL must be loopback; a session token must never be sent to a remote host.' }
}

function Get-Engine {
    if ($script:Engine) { return $script:Engine }
    if ($script:Cli.Opts.ContainsKey('session')) { throw 'Do not pass a session token on the command line. Use the FAIRLEAD_SESSION environment variable or the local CLI access file.' }
    $url = Opt 'url' $env:FAIRLEAD_URL
    if ($url) { Assert-LocalEngineUrl $url }
    $token = $env:FAIRLEAD_SESSION
    if (-not $url -or -not $token) {
        $dataDir = $env:FAIRLEAD_DATA_DIR
        if (-not $dataDir) { $dataDir = Join-Path $env:LOCALAPPDATA 'Fairlead' }
        $file = Join-Path $dataDir 'fairlead-cli.json'
        if (-not (Test-Path -LiteralPath $file)) { throw 'Fairlead is not running (no CLI access file). Start it with: fairlead start' }
        $info = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if (-not (Get-Process -Id $info.pid -ErrorAction SilentlyContinue)) { throw ('Fairlead is not running (the engine that wrote ' + $file + ' has exited). Start it with: fairlead start') }
        if (-not $url) { $url = $info.url }
        Assert-LocalEngineUrl $url
        if (-not $token) {
            Add-Type -AssemblyName System.Security
            $stored = [string]$info.token
            if (-not $stored.StartsWith('dpapi:')) { throw 'The CLI access file holds no token this script understands.' }
            try {
                $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($stored.Substring(6)), [Text.Encoding]::UTF8.GetBytes('Fairlead.CliToken.v1'), 'CurrentUser')
            } catch { throw 'The CLI access file was written by another Windows user; run fairlead as the user that started Fairlead.' }
            $token = [Text.Encoding]::UTF8.GetString($bytes)
        }
    }
    $script:Engine = @{ Url = $url.TrimEnd('/'); Token = $token }
    return $script:Engine
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{}) {
    $e = Get-Engine
    $req = [System.Net.HttpWebRequest]::Create($e.Url + $Path)
    $req.Method = $Method
    $req.Accept = 'application/json'
    $req.Timeout = 600000
    $req.ReadWriteTimeout = 600000
    $req.AllowAutoRedirect = $false
    $req.Headers.Add('X-Fairlead-Session', $e.Token)
    foreach ($k in $Headers.Keys) { $req.Headers.Add($k, $Headers[$k]) }
    if ($Method -ne 'GET' -and $Method -ne 'DELETE') {
        if ($null -eq $Body) { $Body = @{} }
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Body -Depth 30 -Compress))
        $req.ContentType = 'application/json'
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
    }
    try { $resp = $req.GetResponse() }
    catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw ('Cannot reach Fairlead at ' + $e.Url + ': ' + $_.Exception.Message) }
    }
    if ([int]$resp.StatusCode -ge 300 -and [int]$resp.StatusCode -lt 400) {
        $resp.Close()
        throw 'The local Fairlead admin API returned a redirect. Refusing to forward session credentials.'
    }
    $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd(); $reader.Close()
    $status = [int]$resp.StatusCode
    $resp.Close()
    if ($status -ge 400) {
        $msg = $text
        try { $j = $text | ConvertFrom-Json; if ($j.error) { $msg = [string]$j.error } } catch { }
        throw ('HTTP ' + $status + ': ' + $msg)
    }
    if (-not $text) { return $null }
    return ($text | ConvertFrom-Json)
}

function Out-Result($Value, [scriptblock]$Pretty) {
    if (Opt 'json') { ConvertTo-Json -InputObject $Value -Depth 30; return }
    if ($Pretty) { & $Pretty $Value } else { $Value | Format-List | Out-String -Width 160 | Write-Output }
}

function Read-Secret([string]$Prompt) {
    if (Opt 'key-stdin') { return ([Console]::In.ReadToEnd()).Trim() }
    if ([Console]::IsInputRedirected) { return ([Console]::In.ReadToEnd()).Trim() }
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Confirm-Action([string]$What) {
    if (Opt 'yes') { return }
    if ([Console]::IsInputRedirected) { throw "$What needs --yes when run from a script." }
    $answer = Read-Host "$What? [y/N]"
    if ($answer -notmatch '^[Yy]') { throw 'Cancelled.' }
}

function Esc([string]$Value) { return [Uri]::EscapeDataString($Value) }

# ---------- helpers over config ----------

function Get-Config { return Invoke-Api GET '/api/config' }

function Find-Provider($Config, [string]$Id) {
    $p = @($Config.providers | Where-Object { $_.id -eq $Id })
    if ($p.Count -eq 0) { return $null }
    return $p[0]
}

function ConvertTo-ProviderBody($P) {
    # The fields SaveProvider reads; keys and key hints are never sent back.
    return [ordered]@{
        id = $P.id; name = $P.name; kind = $P.kind; baseUrl = $P.baseUrl; local = [bool]$P.local; enabled = [bool]$P.enabled
        apiKeyEnv = $(if ($P.apiKeyEnv) { $P.apiKeyEnv } else { '' }); timeoutSeconds = $P.timeoutSeconds; streamUsage = [bool]$P.streamUsage
        models = @($P.models); headers = $P.headers; retries = $P.retries
    }
}

function Save-Provider($Body, [string]$Key = $null, [bool]$ClearKey = $false) {
    return Invoke-Api POST '/api/providers' @{ provider = $Body; apiKey = $Key; clearKey = $ClearKey }
}

function Get-Route($Config, [string]$Id) {
    $r = @($Config.routes | Where-Object { $_.id -eq $Id })
    if ($r.Count -eq 0) { return $null }
    return $r[0]
}

# ---------- commands ----------

function Show-Help {
@'
fairlead <command> [options]          every command maps to one admin API route (docs\API.md)

Engine
  start [--console] [--no-tray] [--open] [--replace]   start the engine (Launch-Fairlead.bat)
  stop --yes                          stop the running engine            POST /api/session/stop
  startup on|off                      start Fairlead at Windows sign-in (Register-FairleadStartup.ps1)
  status                              engine, connectivity, providers, metrics   GET /api/status
  api                                 list every admin API route         GET /api

Providers
  providers [list]                    GET /api/config
  providers show <id>
  providers set <id> [--preset P] [--name N] [--kind openAi|anthropic|ollama|responses] [--base-url U]
                [--local true|false] [--enabled true|false] [--timeout S] [--key-env VAR] [--retries N]
                [--key-stdin | --clear-key]   create or change; --key-stdin reads the API key from stdin
  providers key <id>                  set the API key (hidden prompt or stdin)
  providers enable|disable <id>
  providers remove <id> --yes         DELETE /api/providers/{id}
  providers test <id>                 POST /api/providers/{id}/test
  providers discover <id>             POST /api/providers/{id}/discover
  presets                             GET /api/presets
  detect                              find local model servers           GET /api/detect

Routes and routing
  routes [list] | show <id>
  routes set <id> [--targets a/b,c/d] [--add-target T] [--remove-target T] [--strategy failover|fastest|roundRobin|auto]
                [--hedge-ms N] [--local-fallback true|false] [--description D] [--rename NEW]   POST /api/routes
  routes remove <id> --yes            DELETE /api/routes/{id}
  routing [show]                      rules, aliases, default and auto-fallback routes
  routing set [--default R] [--auto-fallback R] [--file routing.json]   POST /api/routing
  routing reset --yes                 POST /api/routing/reset
  explain <prompt> [--model M] [--route R] [--client C] [--private]   dry run   POST /api/explain
  chat <prompt> [--model M] [--route R] [--system S] [--max-tokens N] [--tools] [--private]   POST /api/playground

Clients
  clients [list]
  clients add <name> [--routes a,b] [--rate N]   prints the key once; --rate limits requests a minute   POST /api/clients
  clients rate <id|name> <N|off>      limit a key to N requests a minute (off = no limit)   POST /api/clients/{id}/rate-limit
  clients remove <id|name> --yes      DELETE /api/clients/{id}

Settings and Kedge mode
  kedge [auto|on|off]                 POST /api/kedge
  settings [show]
  settings set key=value ...          requireClientKey, logContent, setupComplete, kedge, lazaretteHoldHours,
                                      corsOrigins (a,b), privacy.detectSecrets, privacy.forceLocalWhenSensitive,
                                      network.<field>                    POST /api/settings

AI gateway model files
  gateway [show]
  gateway import <file.yaml>          POST /api/gateway/import
  gateway curl "<curl command>" [--token-variable NAME]   POST /api/gateway/curl
  gateway value <NAME>                store one variable (hidden prompt or stdin)   POST /api/gateway/values
  gateway test <provider/model>       POST /api/gateway/test
  gateway remove [name] --yes         DELETE /api/gateway[/{name}]

Lazarette (keys by name)
  lazarette [status] | pair <code> | unpair --yes | refresh

Health and activity
  health                              providers, models, breakers        GET /api/health/providers
  probe                               check network and providers now    POST /api/probe
  breakers reset [provider]           POST /api/breakers/reset
  requests [--limit N]                GET /api/requests
  request <id>                        GET /api/requests/{id}
  bearing [status]                    GET /api/bearing: cited answers (off unless FAIRLEAD_BEARING_DIR is set)
  bearing search <question> [--limit N]  GET /api/bearing/search: what a question would retrieve
  requestlog [--since 24h] [--client N] [--label L] [--model M] [--route R] [--status ok|error|4xx|5xx|CODE] [--limit N] [--summary]
                                      GET /api/requestlog[/summary]: the durable who/what/which-model log (no prompts)
  metrics                             GET /api/metrics
  logs [--level info|warn|error]      GET /api/logs
  live [--count N]                    stream request events as they happen   GET /api/live

Options for every command: --json (the value as JSON), --url U (loopback only).
Use FAIRLEAD_URL and FAIRLEAD_SESSION for a different local engine. Never put a session token on the command line.
'@
}

function Invoke-Providers {
    $sub = Word 1
    if (-not $sub) { $sub = 'list' }
    switch ($sub) {
        'list' {
            $c = Get-Config
            Out-Result $c.providers { param($v) $v | Select-Object id, name, kind, local, enabled, keySource, @{ n = 'models'; e = { @($_.models).Count + @($_.discovered).Count } }, baseUrl | Format-Table -AutoSize | Out-String -Width 200 }
        }
        'show' {
            $p = Find-Provider (Get-Config) (Word 2 'provider id')
            if (-not $p) { throw ('No provider ' + (Word 2) + '.') }
            Out-Result $p
        }
        'set' {
            $id = Word 2 'provider id'
            $c = Get-Config
            $p = Find-Provider $c $id
            if ($p) { $body = ConvertTo-ProviderBody $p }
            else {
                $preset = Opt 'preset' $id
                $pre = @((Invoke-Api GET '/api/presets') | Where-Object { $_.id -eq $preset })
                if ($pre.Count -eq 0 -and -not (Opt 'base-url')) { throw "No provider '$id' and no preset '$preset'. Give --preset or --kind and --base-url." }
                $body = [ordered]@{ id = $id; name = $id; kind = 'openAi'; baseUrl = ''; local = $false; enabled = $true; apiKeyEnv = ''; timeoutSeconds = 120; streamUsage = $true; models = @() }
                if ($pre.Count -gt 0) {
                    $body.name = $pre[0].name; $body.kind = $pre[0].kind; $body.baseUrl = $pre[0].baseUrl; $body.local = [bool]$pre[0].local
                    if ($pre[0].apiKeyEnv) { $body.apiKeyEnv = $pre[0].apiKeyEnv }
                    if ($pre[0].local) { $body.timeoutSeconds = 600 }
                }
            }
            if (Opt 'name') { $body.name = Opt 'name' }
            if (Opt 'kind') { $body.kind = Opt 'kind' }
            if (Opt 'base-url') { $body.baseUrl = Opt 'base-url' }
            if (Opt 'local') { $body.local = ConvertTo-Bool (Opt 'local') 'local' }
            if (Opt 'enabled') { $body.enabled = ConvertTo-Bool (Opt 'enabled') 'enabled' }
            if (Opt 'timeout') { $body.timeoutSeconds = [int](Opt 'timeout') }
            if (Opt 'key-env') { $body.apiKeyEnv = Opt 'key-env' }
            if (Opt 'retries') { $body.retries = [int](Opt 'retries') }
            $key = $null
            if (Opt 'key-stdin') { $key = Read-Secret 'API key' }
            $null = Save-Provider $body $key ([bool](Opt 'clear-key' $false))
            Write-Output ("Saved provider $id.")
        }
        'key' {
            $id = Word 2 'provider id'
            $p = Find-Provider (Get-Config) $id
            if (-not $p) { throw "No provider $id." }
            $key = Read-Secret ("API key for " + $p.name)
            if (-not $key) { throw 'No key given.' }
            $null = Save-Provider (ConvertTo-ProviderBody $p) $key
            Write-Output ("Stored the key for $id (encrypted for this Windows user).")
        }
        { $_ -eq 'enable' -or $_ -eq 'disable' } {
            $id = Word 2 'provider id'
            $p = Find-Provider (Get-Config) $id
            if (-not $p) { throw "No provider $id." }
            if ($p.managed) { throw "$id comes from the AI gateway model file; change it there." }
            $body = ConvertTo-ProviderBody $p
            $body.enabled = ($sub -eq 'enable')
            $null = Save-Provider $body
            Write-Output ("$id " + $sub + 'd.')
        }
        'remove' {
            $id = Word 2 'provider id'
            Confirm-Action "Remove provider $id"
            $null = Invoke-Api DELETE ('/api/providers/' + (Esc $id))
            Write-Output "Removed provider $id."
        }
        'test' {
            $r = Invoke-Api POST ('/api/providers/' + (Esc (Word 2 'provider id')) + '/test')
            Out-Result $r { param($v) if ($v.ok) { 'Connected in ' + $v.ms + ' ms: ' + @($v.models).Count + ' models.' } else { 'Failed (' + $v.kind + '): ' + $v.error } }
            if (-not $r.ok) { exit 1 }
        }
        'discover' {
            $id = Word 2 'provider id'
            $c = Invoke-Api POST ('/api/providers/' + (Esc $id) + '/discover')
            $p = Find-Provider $c $id
            Out-Result $p { param($v) 'Found ' + @($v.discovered).Count + ' models on ' + $v.id + '.' }
        }
        default { throw "Unknown: providers $sub. Run: fairlead help" }
    }
}

function Invoke-Routes {
    $sub = Word 1
    if (-not $sub) { $sub = 'list' }
    switch ($sub) {
        'list' {
            $c = Get-Config
            Out-Result $c.routes { param($v) $v | Select-Object id, strategy, @{ n = 'targets'; e = { if (@($_.targets).Count) { $_.targets -join ', ' } elseif ($_.strategy -eq 'auto') { '(rules)' } else { '(none: falls back)' } } }, localFallback | Format-Table -AutoSize -Wrap | Out-String -Width 200 }
        }
        'show' {
            $r = Get-Route (Get-Config) (Word 2 'route id')
            if (-not $r) { throw ('No route ' + (Word 2) + '.') }
            Out-Result $r
        }
        'set' {
            $id = Word 2 'route id'
            $r = Get-Route (Get-Config) $id
            $body = [ordered]@{ id = $id; description = ''; strategy = 'failover'; targets = @(); hedgeAfterMs = 0; localFallback = $true }
            if ($r) { $body.description = $r.description; $body.strategy = $r.strategy; $body.targets = @($r.targets); $body.hedgeAfterMs = $r.hedgeAfterMs; $body.localFallback = [bool]$r.localFallback }
            if ($script:Cli.Opts.ContainsKey('targets')) { $body.targets = Split-List (Opt 'targets') }
            if (Opt 'add-target') { $body.targets = @($body.targets) + @(Split-List (Opt 'add-target')) | Select-Object -Unique }
            if (Opt 'remove-target') { $drop = Split-List (Opt 'remove-target'); $body.targets = @($body.targets | Where-Object { $drop -notcontains $_ }) }
            if (Opt 'strategy') { $body.strategy = Opt 'strategy' }
            if (Opt 'hedge-ms') { $body.hedgeAfterMs = [int](Opt 'hedge-ms') }
            if (Opt 'local-fallback') { $body.localFallback = ConvertTo-Bool (Opt 'local-fallback') 'local-fallback' }
            if ($script:Cli.Opts.ContainsKey('description')) { $body.description = Opt 'description' }
            $previous = $null
            if (Opt 'rename') { $previous = $id; $body.id = Opt 'rename' }
            $body.targets = @($body.targets)
            $null = Invoke-Api POST '/api/routes' @{ route = $body; previousId = $previous }
            Write-Output ('Saved route ' + $body.id + ': ' + $(if (@($body.targets).Count) { $body.targets -join ', ' } else { 'no targets' }) + '.')
        }
        'remove' {
            $id = Word 2 'route id'
            Confirm-Action "Remove route $id"
            $null = Invoke-Api DELETE ('/api/routes/' + (Esc $id))
            Write-Output "Removed route $id."
        }
        default { throw "Unknown: routes $sub. Run: fairlead help" }
    }
}

function Invoke-Routing {
    $sub = Word 1
    if (-not $sub) { $sub = 'show' }
    switch ($sub) {
        'show' {
            $c = Get-Config
            $v = [ordered]@{ defaultRoute = $c.defaultRoute; autoFallbackRoute = $c.autoFallbackRoute; rules = $c.rules; aliases = $c.aliases }
            Out-Result $v {
                param($x)
                'Default route: ' + $x.defaultRoute + '    auto falls back to: ' + $x.autoFallbackRoute
                ''
                'Rules (first match wins):'
                $x.rules | Select-Object name, route, enabled, @{ n = 'when'; e = { ($_.when | ConvertTo-Json -Compress) } } | Format-Table -AutoSize -Wrap | Out-String -Width 200
                'Aliases:'
                $x.aliases | Format-List | Out-String -Width 200
            }
        }
        'set' {
            $c = Get-Config
            $body = [ordered]@{ defaultRoute = $c.defaultRoute; autoFallbackRoute = $c.autoFallbackRoute; rules = @($c.rules); aliases = $c.aliases }
            if (Opt 'file') {
                $f = Get-Content -LiteralPath (Opt 'file') -Raw | ConvertFrom-Json
                foreach ($k in @('defaultRoute', 'autoFallbackRoute', 'rules', 'aliases')) { if ($f.PSObject.Properties[$k]) { $body[$k] = $f.$k } }
            }
            if (Opt 'default') { $body.defaultRoute = Opt 'default' }
            if (Opt 'auto-fallback') { $body.autoFallbackRoute = Opt 'auto-fallback' }
            $body.rules = @($body.rules)
            $null = Invoke-Api POST '/api/routing' $body
            Write-Output 'Saved routing.'
        }
        'reset' {
            Confirm-Action 'Reset rules and aliases to the defaults'
            $null = Invoke-Api POST '/api/routing/reset'
            Write-Output 'Routing reset to the defaults.'
        }
        default { throw "Unknown: routing $sub. Run: fairlead help" }
    }
}

function Invoke-Clients {
    $sub = Word 1
    if (-not $sub) { $sub = 'list' }
    switch ($sub) {
        'list' {
            $c = Get-Config
            Out-Result $c.clients { param($v) if (@($v).Count -eq 0) { 'No client keys. Create one: fairlead clients add <name>' } else { $v | Select-Object id, name, prefix, created, @{ n = 'routes'; e = { if (@($_.routes).Count) { $_.routes -join ', ' } else { '(all)' } } }, @{ n = 'perMinute'; e = { if ($_.rateLimitPerMinute) { $_.rateLimitPerMinute } else { '(none)' } } } | Format-Table -AutoSize | Out-String -Width 200 } }
        }
        'add' {
            $payload = @{ name = (Word 2 'client name'); routes = @(Split-List (Opt 'routes')) }
            if (Opt 'rate') { $payload.rateLimitPerMinute = [int](Opt 'rate') }
            $r = Invoke-Api POST '/api/clients' $payload
            Out-Result $r { param($v) 'Client key for ' + (Word 2) + ' (shown once; Fairlead keeps only its hash):'; $v.key }
        }
        'rate' {
            $which = Word 2 'client id or name'
            $k = @((Get-Config).clients | Where-Object { $_.id -eq $which -or $_.name -eq $which })
            if ($k.Count -eq 0) { throw "No client key $which." }
            if ($k.Count -gt 1) { throw "Several client keys are named $which; give the id." }
            $value = Word 3 'requests a minute, or off'
            $n = 0
            if ($value -ne 'off') { if (-not [int]::TryParse($value, [ref]$n) -or $n -lt 1) { throw 'Give a whole number of requests a minute, or off.' } }
            $r = Invoke-Api POST ('/api/clients/' + (Esc $k[0].id) + '/rate-limit') @{ requestsPerMinute = $n }
            Out-Result $r { param($v) if ($v.rateLimitPerMinute) { $k[0].name + ' is limited to ' + $v.rateLimitPerMinute + ' requests a minute.' } else { $k[0].name + ' has no request limit.' } }
        }
        'remove' {
            $which = Word 2 'client id or name'
            $k = @((Get-Config).clients | Where-Object { $_.id -eq $which -or $_.name -eq $which })
            if ($k.Count -eq 0) { throw "No client key $which." }
            if ($k.Count -gt 1) { throw "Several client keys are named $which; give the id." }
            Confirm-Action ('Revoke client key ' + $k[0].name)
            $null = Invoke-Api DELETE ('/api/clients/' + (Esc $k[0].id))
            Write-Output ('Revoked ' + $k[0].name + '.')
        }
        default { throw "Unknown: clients $sub. Run: fairlead help" }
    }
}

function Invoke-Settings {
    $sub = Word 1
    if (-not $sub) { $sub = 'show' }
    $c = Get-Config
    if ($sub -eq 'show') {
        $v = [ordered]@{ setupComplete = $c.setupComplete; requireClientKey = $c.requireClientKey; kedge = $c.kedge; logContent = $c.logContent; lazaretteHoldHours = $c.lazaretteHoldHours; corsOrigins = $c.corsOrigins; privacy = $c.privacy; network = $c.network }
        Out-Result $v { param($x) $x | ConvertTo-Json -Depth 5 }
        return
    }
    if ($sub -ne 'set') { throw "Unknown: settings $sub. Run: fairlead help" }
    $pairs = Rest 2
    if ($pairs.Count -eq 0) { throw 'Give one or more key=value pairs.' }
    $body = @{}
    foreach ($pair in $pairs) {
        $eq = $pair.IndexOf('=')
        if ($eq -le 0) { throw "Expected key=value, got '$pair'." }
        $k = $pair.Substring(0, $eq); $v = $pair.Substring($eq + 1)
        switch -Regex ($k) {
            '^(requireClientKey|logContent|setupComplete)$' { $body[$k] = ConvertTo-Bool $v $k }
            '^kedge$' { $body.kedge = $v }
            '^lazaretteHoldHours$' { $body.lazaretteHoldHours = [double]$v }
            '^corsOrigins$' { $body.corsOrigins = @(Split-List $v) }
            '^(privacy|network)\.(.+)$' {
                $group = $Matches[1]; $field = $Matches[2]
                if (-not $body.ContainsKey($group)) { $body[$group] = @{}; foreach ($p in $c.$group.PSObject.Properties) { $body[$group][$p.Name] = $p.Value } }
                if (-not $body[$group].ContainsKey($field)) { throw "Unknown setting $k." }
                $old = $body[$group][$field]
                if ($old -is [bool]) { $body[$group][$field] = ConvertTo-Bool $v $k }
                elseif ($old -is [array] -or $old -is [Object[]]) { $body[$group][$field] = @(Split-List $v) }
                elseif ($old -is [int] -or $old -is [long] -or $old -is [double] -or $old -is [decimal]) { $body[$group][$field] = [double]$v }
                else { $body[$group][$field] = $v }
            }
            default { throw "Unknown setting $k. Run: fairlead help" }
        }
    }
    $null = Invoke-Api POST '/api/settings' $body
    Write-Output 'Saved settings.'
}

function Invoke-Gateway {
    $sub = Word 1
    if (-not $sub) { $sub = 'show' }
    switch ($sub) {
        'show' { Out-Result (Invoke-Api GET '/api/status').gatewayModels }
        'import' {
            $path = Word 2 'model file path'
            $r = Invoke-Api POST '/api/gateway/import' @{ yaml = (Get-Content -LiteralPath $path -Raw); fileName = (Split-Path $path -Leaf) }
            Out-Result $r { param($v) if ($v.saved) { 'Imported ' + (Split-Path $path -Leaf) + '.' } else { 'Not imported:'; @($v.errors) }; @($v.warnings) | ForEach-Object { 'warning: ' + $_ } }
        }
        'curl' {
            $r = Invoke-Api POST '/api/gateway/curl' @{ curl = (Word 2 'curl command'); tokenVariable = (Opt 'token-variable') }
            Out-Result $r { param($v) if ($v.saved) { 'Added ' + $v.target + ' from the curl command (token variable ' + $v.tokenVariable + ').' } else { 'Not added:'; @($v.errors) } }
        }
        'value' {
            $name = Word 2 'variable name'
            $value = Read-Secret "Value for $name"
            if (-not $value) { throw 'No value given.' }
            $null = Invoke-Api POST '/api/gateway/values' @{ values = @{ $name = $value } }
            Write-Output "Stored $name (encrypted for this Windows user)."
        }
        'test' {
            $r = Invoke-Api POST '/api/gateway/test' @{ target = (Word 2 'provider/model') }
            Out-Result $r { param($v) $v.verdict + ': ' + $v.message; if ($v.detail) { $v.detail } }
            if (-not $r.ok) { exit 1 }
        }
        'remove' {
            $name = Word 2
            Confirm-Action $(if ($name) { "Remove gateway model file $name" } else { 'Remove every gateway model file and the saved address' })
            $null = Invoke-Api DELETE $(if ($name) { '/api/gateway/' + (Esc $name) } else { '/api/gateway' })
            Write-Output 'Removed.'
        }
        default { throw "Unknown: gateway $sub. Run: fairlead help" }
    }
}

function Invoke-Lazarette {
    $sub = Word 1
    if (-not $sub) { $sub = 'status' }
    switch ($sub) {
        'status' { Out-Result (Invoke-Api GET '/api/lazarette') }
        'pair' { Out-Result (Invoke-Api POST '/api/lazarette/pair' @{ code = (Word 2 'pairing code') }) }
        'unpair' { Confirm-Action 'Unpair from Lazarette'; Out-Result (Invoke-Api DELETE '/api/lazarette/pair') }
        'refresh' { Out-Result (Invoke-Api POST '/api/lazarette/refresh') }
        default { throw "Unknown: lazarette $sub. Run: fairlead help" }
    }
}

function Get-PromptBody {
    $prompt = (Rest 1) -join ' '
    if (-not $prompt) { throw 'Give a prompt.' }
    $b = @{ prompt = $prompt; model = (Opt 'model' 'auto') }
    if (Opt 'route') { $b.route = Opt 'route' }
    if (Opt 'private') { $b.private = $true }
    if (Opt 'tools') { $b.withTools = $true }
    if (Opt 'system') { $b.system = Opt 'system' }
    if (Opt 'max-tokens') { $b.maxTokens = [int](Opt 'max-tokens') }
    if (Opt 'client') { $b.client = Opt 'client' }
    return $b
}

function Invoke-Live {
    $e = Get-Engine
    $req = [System.Net.HttpWebRequest]::Create($e.Url + '/api/live')
    $req.Headers.Add('X-Fairlead-Session', $e.Token)
    $req.Accept = 'text/event-stream'
    $req.Timeout = [Threading.Timeout]::Infinite
    $req.ReadWriteTimeout = [Threading.Timeout]::Infinite
    try { $resp = $req.GetResponse() }
    catch [System.Net.WebException] {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { throw 'This engine has no live feed (/api/live); update Fairlead.' }
        throw
    }
    $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $max = [int](Opt 'count' 0)
    $seen = 0
    $type = 'message'
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line.StartsWith('event: ')) { $type = $line.Substring(7); continue }
            if (-not $line.StartsWith('data: ')) { continue }
            $data = $line.Substring(6)
            if ($type -eq 'hello') { if (-not (Opt 'json')) { Write-Host 'Watching live requests (Ctrl+C to stop)...' }; continue }
            if (Opt 'json') { Write-Output ('{"event":"' + $type + '","data":' + $data + '}') }
            else {
                $d = $data | ConvertFrom-Json
                $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $d.id + ' ' + $type
                switch ($type) {
                    'start' { $line += ' ' + $(if ($d.client) { $d.client } else { '(no key)' }) + ' ' + $d.requestedModel + ' -> ' + $d.route }
                    'attempt' { $line += ' ' + $d.phase + ' ' + $d.target + $(if ($d.phase -eq 'end') { ' ' + $d.outcome + $(if ($d.error) { ': ' + $d.error } else { '' }) } else { '' }) }
                    'firstByte' { $line += ' ' + $d.ms + ' ms' }
                    'done' { $line += ' ' + $d.status + ' ' + $d.target + ' ' + $d.totalMs + ' ms' + $(if ($d.error) { ' ' + $d.error } else { '' }) }
                }
                Write-Output $line
            }
            if ($type -eq 'done') { $seen++; if ($max -gt 0 -and $seen -ge $max) { break } }
            $type = 'message'
        }
    } finally { $reader.Close(); $resp.Close() }
}

function Invoke-Fairlead([string[]]$List) {
    $script:Cli = Split-Arguments $List
    $cmd = Word 0
    if (-not $cmd) { $cmd = 'help' }
    switch ($cmd) {
        { $_ -in @('help', '-h', '--help', '/?') } { Show-Help }
        'start' {
            $pass = @()
            foreach ($f in @('console', 'no-tray', 'no-browser', 'open', 'replace')) { if (Opt $f) { $pass += '--' + $f } }
            & (Join-Path $script:Root 'scripts\Start-Fairlead.ps1') @pass
        }
        'stop' {
            Confirm-Action 'Stop Fairlead (tools using the gateway lose it)'
            $null = Invoke-Api POST '/api/session/stop' @{} @{ 'X-Fairlead-Intent' = 'end-session' }
            Write-Output 'Fairlead is stopping.'
        }
        'startup' {
            $mode = Word 1 'on or off'
            if ($mode -eq 'on') { & (Join-Path $script:Root 'scripts\Register-FairleadStartup.ps1') }
            elseif ($mode -eq 'off') { & (Join-Path $script:Root 'scripts\Register-FairleadStartup.ps1') -Remove }
            else { throw 'fairlead startup on|off' }
        }
        'status' {
            $s = Invoke-Api GET '/api/status'
            Out-Result $s {
                param($v)
                $m = $v.metrics
                'Fairlead ' + $v.version + '  gateway ' + $v.gatewayUrl + '  setup ' + $(if ($v.setupComplete) { 'complete' } else { 'not finished' })
                'Internet ' + $v.connectivity.internet + $(if ($v.connectivity.kedge) { '  KEDGE MODE (local only)' } else { '' }) + '  client keys ' + $v.clients + $(if ($v.requireClientKey) { ' (required)' } else { ' (optional)' })
                'Requests ' + $m.total + ' since start, ' + $m.lastHour.requests + ' in the last hour (' + $m.lastHour.failed + ' failed)'
                ''
                $v.providers | Select-Object id, name, local, enabled, reachable, breaker, keySource, models | Format-Table -AutoSize | Out-String -Width 200
                $empty = @($v.routes | Where-Object { $_.strategy -ne 'auto' -and $_.targets -eq 0 } | ForEach-Object { $_.id })
                if ($empty.Count) { 'Routes with no targets (requests fall back to local): ' + ($empty -join ', ') }
            }
        }
        'api' { Out-Result (Invoke-Api GET '/api') { param($v) $v.routes } }
        'health' { Out-Result (Invoke-Api GET '/api/health/providers') }
        'config' { Out-Result (Get-Config) { param($v) $v | ConvertTo-Json -Depth 10 } }
        'presets' { Out-Result (Invoke-Api GET '/api/presets') { param($v) $v | Select-Object id, name, kind, local, baseUrl | Format-Table -AutoSize | Out-String -Width 200 } }
        'detect' { Out-Result (Invoke-Api GET '/api/detect') { param($v) $v | Select-Object preset, name, running, configured, @{ n = 'models'; e = { @($_.models).Count } }, baseUrl | Format-Table -AutoSize | Out-String -Width 200 } }
        'providers' { Invoke-Providers }
        'routes' { Invoke-Routes }
        'routing' { Invoke-Routing }
        'clients' { Invoke-Clients }
        'settings' { Invoke-Settings }
        'kedge' {
            $mode = Word 1
            if (-not $mode) { $c = Get-Config; $s = Invoke-Api GET '/api/status'; Out-Result $s.connectivity { param($v) 'Kedge setting: ' + $c.kedge + '; active now: ' + $v.kedge + ' (internet ' + $v.internet + ')' }; return }
            if ($mode -notin @('auto', 'on', 'off')) { throw 'fairlead kedge auto|on|off' }
            Out-Result (Invoke-Api POST '/api/kedge' @{ mode = $mode }) { param($v) 'Kedge mode ' + $v.mode + '; active now: ' + $v.connectivity.kedge }
        }
        'gateway' { Invoke-Gateway }
        'lazarette' { Invoke-Lazarette }
        'probe' { Out-Result (Invoke-Api POST '/api/probe') }
        'breakers' {
            if ((Word 1) -eq 'reset') {
                $p = Word 2
                $r = Invoke-Api POST '/api/breakers/reset' $(if ($p) { @{ provider = $p } } else { @{} })
                Out-Result $r { param($v) 'Reset breakers: ' + $v.reset }
            } else {
                Out-Result (Invoke-Api GET '/api/health/providers').breakers { param($v) if (@($v).Count -eq 0) { 'No breakers have tripped.' } else { $v | Format-Table -AutoSize | Out-String -Width 200 } }
            }
        }
        'requests' {
            $r = Invoke-Api GET ('/api/requests?limit=' + [int](Opt 'limit' 20))
            Out-Result $r { param($v) $v | Select-Object @{ n = 'time'; e = { ([DateTime]$_.time).ToLocalTime().ToString('HH:mm:ss') } }, id, client, requestedModel, route, target, status, totalMs, failover, kedge | Format-Table -AutoSize | Out-String -Width 200 }
        }
        'bearing' {
            if ((Word 1) -eq 'search') {
                $q = Word 2 'question'
                $r = Invoke-Api GET ('/api/bearing/search?q=' + (Esc $q) + '&limit=' + [int](Opt 'limit' 4))
                Out-Result $r { param($v) @($v) | Select-Object score, source, location, heading | Format-Table -AutoSize | Out-String -Width 200 }
            } else {
                Out-Result (Invoke-Api GET '/api/bearing') { param($v) if ($v.enabled) { 'Bearing is on: ' + $v.folder + ' (' + $v.files + ' files, ' + $v.passages + ' passages). Opt in per request with ' + $v.optIn } else { 'Bearing is off. Set FAIRLEAD_BEARING_DIR to a folder of documents and restart Fairlead.' } }
            }
        }
        'requestlog' {
            $qs = New-Object System.Collections.ArrayList
            foreach ($k in @('since', 'until', 'client', 'label', 'model', 'route', 'status', 'limit')) { $v = Opt $k; if ($v) { [void]$qs.Add($k + '=' + (Esc ([string]$v))) } }
            $suffix = ''; if ($qs.Count) { $suffix = '?' + ($qs -join '&') }
            if (Opt 'summary') {
                $r = Invoke-Api GET ('/api/requestlog/summary' + $suffix)
                Out-Result $r { param($v)
                    'Requests ' + $v.requests + ' (' + $v.failed + ' failed), tokens in ' + $v.inputTokens + ' out ' + $v.outputTokens
                    ''
                    'By client'; $v.byClient | Select-Object name, requests, failed, inputTokens, outputTokens, p50Ms, p95Ms | Format-Table -AutoSize | Out-String -Width 200
                    'By model'; $v.byModel | Select-Object name, requests, failed, inputTokens, outputTokens, p50Ms, p95Ms | Format-Table -AutoSize | Out-String -Width 200
                }
            } else {
                $r = Invoke-Api GET ('/api/requestlog' + $suffix)
                Out-Result $r { param($v) @($v) | Select-Object @{ n = 'time'; e = { ([DateTime]$_.time).ToLocalTime().ToString('MM-dd HH:mm:ss') } }, client, label, endpoint, route, servedBy, status, totalMs, inputTokens, outputTokens | Format-Table -AutoSize | Out-String -Width 220 }
            }
        }
        'request' { Out-Result (Invoke-Api GET ('/api/requests/' + (Esc (Word 1 'request id')))) { param($v) $v | ConvertTo-Json -Depth 6 } }
        'metrics' { Out-Result (Invoke-Api GET '/api/metrics') { param($v) $v | ConvertTo-Json -Depth 6 } }
        'logs' {
            $path = '/api/logs'
            if (Opt 'level') { $path += '?level=' + (Esc (Opt 'level')) }
            Out-Result (Invoke-Api GET $path) { param($v) $v | Select-Object time, level, event, message | Format-Table -AutoSize -Wrap | Out-String -Width 200 }
        }
        'explain' {
            $r = Invoke-Api POST '/api/explain' (Get-PromptBody)
            Out-Result $r { param($v) 'Route: ' + $v.route + $(if ($v.error) { '  ERROR: ' + $v.error } else { '' }); $v.decision | ForEach-Object { '  ' + $_ }; 'Candidates: ' + (@($v.candidates | ForEach-Object { $_.target }) -join ', ') }
        }
        'chat' {
            $r = Invoke-Api POST '/api/playground' (Get-PromptBody)
            Out-Result $r { param($v) if ($v.error) { 'HTTP ' + $v.status + ': ' + $v.error } else { $v.text; ''; '(' + $v.route + ' -> ' + $v.target + ')' } }
            if ($r.status -ge 400) { exit 1 }
        }
        'live' { Invoke-Live }
        default { throw "Unknown command '$cmd'. Run: fairlead help" }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-Fairlead @($Argv | Where-Object { $null -ne $_ }) }
    catch { [Console]::Error.WriteLine('fairlead: ' + $_.Exception.Message); exit 1 }
}
