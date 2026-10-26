# Development

## Prerequisites (build machine only)

The workstation needs none of this: it compiles the engine from source at start and serves the committed `web/dist`.
The build machine is for tests and for UI changes.

- .NET 8 SDK (runs the xUnit tests; builds `net48` too, via reference assemblies). The core is C# 5
  (`LangVersion` 5) with no package references, so it also compiles with the workstation's `Add-Type`.
- Node.js 20+ (rebuild `web/dist` after a UI change and commit it)
- Windows PowerShell 5.1 with Pester 5 (`Install-Module Pester -Scope CurrentUser`)
- Edge or Chrome, only to regenerate brand assets

## Layout

```
Launch-Fairlead.bat          the only launcher
module/Fairlead/             PowerShell module (launcher, tray)
scripts/                     Start-, Stop-Fairlead, process helpers, Register-FairleadStartup
src/Fairlead.Core/           C# core (net48 + netstandard2.0)
src/Fairlead.Core.Tests/     xUnit: translation, routing, breakers, gateway end to end over real sockets
web/                         React + TypeScript admin UI (build.mjs → web/dist)
tests/pester/                PowerShell tests and repository rules
integrations/                per-tool setup and the hackathon worklist
config/                      fairlead.env.example
docs/                        this documentation and the shared Fleet standards
build/                       Build-Fairlead.ps1 (test + bundle), New-FairleadBrand.ps1
```

## Build and test

```powershell
build\Build-Fairlead.ps1                 # everything: core, tests, UI, Pester, bundle in dist\
dotnet test Fairlead.sln                 # core on net8.0 and net48
cd web; npm install; npm run build       # UI only → web\dist
cd web; npm test; npm run typecheck
Invoke-Pester tests\pester
```

Run from source: `dotnet build -c Release`, build the UI, then `.\Launch-Fairlead.bat --console --no-tray`. The
module finds the DLL under `src\Fairlead.Core\bin\Release\net48` and the UI under `web\dist`. To keep your real
config untouched while experimenting, set `FAIRLEAD_DATA_DIR` and `FAIRLEAD_PORT` first.

UI work: `cd web; npm run dev` rebuilds on save; reload the browser.

## Extending

| To add | Do |
|---|---|
| An OpenAI-compatible provider | Add a preset in `Config/ProviderPresets.cs`. Nothing else. |
| A new wire dialect (Gemini native, Bedrock, Ollama `/api/chat`) | Add a `ProviderKind`, endpoint mapping in `UpstreamClient.EndpointUri`, and translators next to `Protocol/Translate.cs`. Keep `UpstreamClient` the only outbound HTTP (a Pester rule checks it). |
| An inbound API (Ollama-compatible, Responses API) | A route in `Host/ApiRoutes.cs` → a handler in `GatewayEngine`, translating into one of the two dialects. |
| A routing signal | A field on `RequestProfile`, a condition on `RuleCondition`, a check in `RoutePlanner.Matches`, a field in the Rules editor. |
| A smarter classifier (embedding or LLM judge) | Compute it in `RequestProfile.From`, expose it as a condition; keep it optional and fast (it runs on every request). |

## Conventions

- PowerShell must run on 5.1: no `??`, `?.`, `&&`/`||` pipeline chains (Pester checks the parse tokens), ASCII only.
- Core must compile for `net48`: stick to the `netstandard2.0` API surface. Warnings are errors.
- Only `Providers/UpstreamClient.cs` makes outbound HTTP calls (Pester rule).
- The browser never sees an API key, a key hash or the DPAPI blob; tests assert it.
- Never show invented numbers: unknown prices, tokens and latencies render as "—".
- Nothing organisation-specific in the repository: no real hostnames, keys or team names.
- Commit messages and PR bodies carry no tool attribution lines.

## CI

`.github/workflows/ci.yml` runs on the self-hosted Windows runner (`self-hosted, Windows, X64`): core build and
tests on `net8.0` and `net48`, UI typecheck/tests/build, Pester on Windows PowerShell 5.1, and uploads the bundle.
