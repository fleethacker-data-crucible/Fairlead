# Architecture

Fairlead is one local process that every AI tool on the machine talks to instead of talking to model providers
directly. It runs on the operator's workstation, serves an OpenAI- and Anthropic-compatible gateway on
`127.0.0.1:8776`, and is configured once through a browser page served by the same process.

## Constraints that shape everything

| Constraint | Consequence |
|---|---|
| Workstation runs Windows PowerShell 5.1 on .NET Framework 4.8; no installs, no admin | Runtime is PowerShell + one .NET library + static web files. No Node, Python, services or installers on the workstation. |
| Fairlead serves other tools, not just its own page | It must outlive the browser tab. It runs as a tray app (the TechStack tray exception). Closing the browser never stops it; only Quit in the tray, End session or `scripts\Stop-Fairlead.ps1` do. |
| Tools hard-code base URLs | The registry port 8776 should never move. If it is taken, Fairlead binds a free port and says so loudly in the console, the tray and the UI. |
| Cloud access comes and goes (trains, conference Wi-Fi, outages) | Connectivity is watched continuously; local models take over without the tool noticing. |
| Keys are valuable | Stored with DPAPI for the Windows user; never returned to the browser; never logged. |
| Build happens off the workstation | The core as C# source and the built UI ship in a bundle; nothing is fetched at runtime. |
| Application control blocks custom DLLs | No DLL is shipped or loaded: Windows PowerShell 5.1 compiles the core's C# 5 source in memory at launch (`Add-Type`), against framework assemblies only. |

## Stack

| Layer | Choice | Why |
|---|---|---|
| Launcher | `Launch-Fairlead.bat` → `powershell.exe -STA -File scripts\Start-Fairlead.ps1` | The only entry point the workstation allows; STA for the WinForms tray icon. |
| Shell | PowerShell module `module/Fairlead` | Reads `config\fairlead.env`, compiles the core, shows the tray icon, opens the browser on first run. Nothing else. |
| Core | C# 5 source `src/Fairlead.Core`, compiled in memory by `Add-Type` at launch | Real concurrency for many simultaneous streams, testable logic. References `System`, `System.Core`, `System.Net.Http` and `System.Security` only; `Fairlead.Core.csproj` is the C# 5 check build, its DLL is never shipped. |
| JSON | Own `JObject`/`JArray` model (`Serialization/`, shared with Halyard) | No third-party assembly. Same API subset and byte-for-byte output as Newtonsoft.Json in 0.1 (parity tests); `JObject` keeps unknown fields so same-dialect relays lose nothing. |
| Server | `TcpListener` with chunked responses | `HttpListener` needs URL reservations a standard user lacks. Chunked transfer streams tokens as they arrive. |
| Upstream | One shared `HttpClient`, system proxy, no redirects | Connection pooling; TLS 1.2 and 64 connections per host forced on .NET Framework. |
| AI gateway | `Config/GatewayModelFile.cs` + `Protocol/ResponsesApi.cs` | Endpoints from the gateway's own model file become read-only Responses providers; base URL and token from environment variables. See [AI gateway](AI-GATEWAY.md). |
| UI | React 19 + TypeScript, Tailwind 4 on the Fleet tokens, zod | Built to static files by esbuild + Tailwind's compiler; every response validated. |
| Config | `%LOCALAPPDATA%\Fairlead\fairlead.json`, atomic writes with a `.bak` | Readable, diffable, survives a crash mid-save. |
| Tests | xUnit (core, with real loopback sockets and a fake upstream), Pester 5 (PowerShell and repository rules), node:test (UI libs) | Each layer in its own tool. |

## Components

```
Launch-Fairlead.bat
  └─ scripts/Start-Fairlead.ps1 (single-instance guard)
       └─ module/Fairlead (PowerShell: env file, in-memory compile of src/Fairlead.Core, tray icon)
            └─ Fairlead.Core (in-memory assembly)
                 ├─ Host        loopback server (chunked streaming), admin API, gateway routes, static UI, session token
                 ├─ Gateway     GatewayEngine: authenticate client → profile → plan → attempts (+hedge) → relay/translate → record
                 ├─ Routing     RequestProfile (tokens, tools, images, secrets), RoutePlanner (routes, rules, aliases, filters, order)
                 ├─ Protocol    SSE reader/writer; OpenAI ⇄ Anthropic requests, responses and streams
                 ├─ Providers   UpstreamClient (the only outbound HTTP), failure classification, model discovery
                 ├─ Resilience  CircuitBreaker per provider and provider/model; HealthMonitor (network events, probes, Kedge mode)
                 ├─ Telemetry   request log, per-target EWMA latency, tokens, cost
                 ├─ Config      fairlead.json store (validate → save → publish), presets, DPAPI secrets, env settings
                 └─ Serialization  JSON document model, reader/writer and object mapping (replaces Newtonsoft.Json)
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

Steps 3–6 are also published as they happen (`start`, `attempt` begin/end, `firstByte`, `done`) on `GET /api/live`,
a server-sent-events stream behind the same loopback check and session token as the rest of `/api`. The Overview's
routing map reads it with `fetch` (so the token stays in a header) and lights each request's real path: client →
route (via `auto`'s rules, or the jump to `local` when a route is empty) → each target tried. Nothing is written to
disk, a slow subscriber drops events instead of holding memory, and an engine without the endpoint is polled instead.

Failover happens only before the first byte reaches the caller. After that, an upstream failure ends the stream
with an error event in the caller's dialect; replaying a half-delivered answer from another model would corrupt it.

## Security

- Admin pages and `/api` are served to loopback only, check the `Host` header (DNS rebinding) and require the
  random per-session token in `X-Fairlead-Session`. The tray and the launcher hold the token; it is never written
  to the runtime file.
- `/v1` requires a client key (`fl-…`) unless the operator turns that off. Fairlead binds 127.0.0.1 only, and the
  server refuses any non-loopback peer as a second line of defence.
- Browser origins are refused on `/v1` unless listed, so a web page cannot spend the operator's credits.
- API keys: DPAPI (CurrentUser) at rest; the browser sees only where a key comes from and its last four characters.
  A stored key is dropped when its provider's base URL changes, so a key cannot be redirected without retyping it.
- Upstream redirects are not followed. Logs and previews pass through a redactor for token-shaped strings.
  Prompt text is kept only in memory and only when content logging is on.

## Risk to watch

In-memory compilation with `Add-Type` must stay allowed for standard users. If PowerShell runs in Constrained
Language mode, or a policy blocks the compile, `Import-FairleadCore` stops with a clear message and asks the operator
to involve IT; it never works around the policy. The compile takes a few seconds per launch
(`Get-FairleadCoreLoadInfo`; CI reports it from `build\Test-FairleadAddType.ps1`).
