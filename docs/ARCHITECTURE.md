# Architecture

Fairlead is one local process that every AI tool on the machine talks to instead of talking to model providers
directly. It runs on the operator's workstation, serves an OpenAI- and Anthropic-compatible gateway on
`127.0.0.1:8776`, and is configured once through a browser page served by the same process.

## Constraints that shape everything

| Constraint | Consequence |
|---|---|
| Workstation runs Windows PowerShell 5.1 on .NET Framework 4.8; no installs, no admin | Runtime is PowerShell + one .NET library + static web files. No Node, Python, services or installers on the workstation. |
| Fairlead serves other tools, not just its own page | It must outlive the browser tab. It runs as a tray app (the TechStack tray exception); `FAIRLEAD_AUTO_STOP=1` restores tab ownership. |
| Tools hard-code base URLs | The registry port 8776 should never move. If it is taken, Fairlead binds a free port and says so loudly in the console, the tray and the UI. |
| Cloud access comes and goes (trains, conference Wi-Fi, outages) | Connectivity is watched continuously; local models take over without the tool noticing. |
| Keys are valuable | Stored with DPAPI for the Windows user; never returned to the browser; never logged. |
| Build happens off the workstation | Compiled DLLs and the built UI ship in a bundle; nothing is fetched at runtime. |

## Stack

| Layer | Choice | Why |
|---|---|---|
| Launcher | `Launch-Fairlead.bat` → `powershell.exe -STA -File scripts\Start-Fairlead.ps1` | The only entry point the workstation allows; STA for the WinForms tray icon. |
| Shell | PowerShell module `module/Fairlead` | Reads `config\fairlead.env`, loads the DLL, shows the tray icon, opens the browser on first run. Nothing else. |
| Core | C# library `Fairlead.Core` (`net48` + `netstandard2.0`) | Real concurrency for many simultaneous streams, testable logic. `net48` loads in PowerShell 5.1. |
| JSON | Newtonsoft.Json 13 (bundled) | Identical on .NET Framework and .NET; `JObject` keeps unknown fields so same-dialect relays lose nothing. |
| Server | `TcpListener` with chunked responses | `HttpListener` needs URL reservations a standard user lacks. Chunked transfer streams tokens as they arrive. |
| Upstream | One shared `HttpClient`, system proxy, no redirects | Connection pooling; TLS 1.2 and 64 connections per host forced on .NET Framework. |
| UI | React 19 + TypeScript, Tailwind 4 on the Fleet tokens, zod | Built to static files by esbuild + Tailwind's compiler; every response validated. |
| Config | `%LOCALAPPDATA%\Fairlead\fairlead.json`, atomic writes with a `.bak` | Readable, diffable, survives a crash mid-save. |
| Tests | xUnit (core, with real loopback sockets and a fake upstream), Pester 5 (PowerShell and repository rules), node:test (UI libs) | Each layer in its own tool. |

## Components

```
Launch-Fairlead.bat
  └─ scripts/Start-Fairlead.ps1 (single-instance guard)
       └─ module/Fairlead (PowerShell: env file, DLL load, tray icon)
            └─ Fairlead.Core.dll
                 ├─ Host        loopback server (chunked streaming), admin API, gateway routes, static UI, session token
                 ├─ Gateway     GatewayEngine: authenticate client → profile → plan → attempts (+hedge) → relay/translate → record
                 ├─ Routing     RequestProfile (tokens, tools, images, secrets), RoutePlanner (routes, rules, aliases, filters, order)
                 ├─ Protocol    SSE reader/writer; OpenAI ⇄ Anthropic requests, responses and streams
                 ├─ Providers   UpstreamClient (the only outbound HTTP), failure classification, model discovery
                 ├─ Resilience  CircuitBreaker per provider and provider/model; HealthMonitor (network events, probes, Kedge mode)
                 ├─ Telemetry   request log, per-target EWMA latency, tokens, cost
                 └─ Config      fairlead.json store (validate → save → publish), presets, DPAPI secrets, env settings
```

## The request path

```
tool ──POST /v1/messages──▶ Host (origin check, CORS) ──▶ GatewayEngine
   1. client key (Bearer or x-api-key; fl-…, SHA-256 compared)
   2. RequestProfile: estimated tokens, tools, images, latest user text, secrets scan
   3. RoutePlanner: header → route name → provider/model → alias → served model → default route
                    auto route → first matching rule → route
                    filters: Kedge / private (local only), capabilities, context window
                    order: failover | fastest (EWMA) | round-robin; open breakers last; empty → local route → any loaded local model
   4. attempts: translate if the dialects differ, send, wait for headers; on failure classify and try the next;
                hedge: after N ms start the next target too, first headers win, the loser is cancelled
   5. response: same dialect → relay bytes (reading usage on the way); other dialect → translate event by event
   6. record: decision trace, attempts, tokens, first-token time, cost → Activity, metrics, logs\fairlead-requests-*.jsonl
```

Failover happens only before the first byte reaches the caller. After that, an upstream failure ends the stream
with an error event in the caller's dialect; replaying a half-delivered answer from another model would corrupt it.

## Security

- Admin pages and `/api` are served to loopback only, check the `Host` header (DNS rebinding) and require the
  random per-session token in `X-Fairlead-Session`. The tray and the launcher hold the token; it is never written
  to the runtime file.
- `/v1` requires a client key (`fl-…`) unless the operator turns that off for loopback. Remote callers
  (`FAIRLEAD_BIND=lan`) always need a key and can reach `/v1` only.
- Browser origins are refused on `/v1` unless listed, so a web page cannot spend the operator's credits.
- API keys: DPAPI (CurrentUser) at rest; the browser sees only where a key comes from and its last four characters.
  A stored key is dropped when its provider's base URL changes, so a key cannot be redirected without retyping it.
- Upstream redirects are not followed. Logs and previews pass through a redactor for token-shaped strings.
  Prompt text is kept only in memory and only when content logging is on.

## Risk to watch

`Fairlead.Core.dll` is unsigned. If the workstation's application-control policy blocks unsigned DLLs,
`Import-FairleadCore` stops with a clear message; have IT sign or allow it.
