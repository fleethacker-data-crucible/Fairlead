# Development

## Prerequisites (build machine only)

- .NET 8 SDK (builds `net48` too, via reference assemblies)
- Node.js 20+
- Windows PowerShell 5.1 with Pester 5 (`Install-Module Pester -Scope CurrentUser`)
- Edge or Chrome, only to regenerate brand assets

## Layout

```
Launch-Fairlead.bat          the only launcher
module/Fairlead/             PowerShell module (launcher, tray)
scripts/                     Start-, Stop-Fairlead, process helpers, Register-FairleadStartup
src/Fairlead.Core/           C# 5 core, shipped as source (the .csproj is only the C# 5 check build)
src/Fairlead.Core.Tests/     xUnit (compiles the core .cs files): translation, routing, breakers, gateway over real sockets, Newtonsoft parity
web/                         React + TypeScript admin UI (build.mjs → web/dist)
tests/pester/                PowerShell tests and repository rules
integrations/                per-tool setup and the hackathon worklist
config/                      fairlead.env.example
docs/                        this documentation and the shared Fleet standards
build/                       Build-Fairlead.ps1 (test + bundle), Test-FairleadAddType.ps1 (PS 5.1 compile check), New-FairleadBrand.ps1
```

## Build and test

```powershell
build\Build-Fairlead.ps1                 # everything: core, tests, UI, Pester, bundle in dist\
dotnet test Fairlead.sln                 # C# 5 check build, then the core on net8.0 and net48
build\Test-FairleadAddType.ps1           # compile the core exactly as the launcher does (Windows PowerShell 5.1)
cd web; npm install; npm run build       # UI only → web\dist
cd web; npm test; npm run typecheck
Invoke-Pester tests\pester
```

Run from source: `.\Launch-Fairlead.bat --console --no-tray` (the built UI is committed in `web\dist`; after a UI
change run `npm run build` in `web` and commit `web\dist`, or CI fails). The module compiles
`src\Fairlead.Core\**\*.cs` in memory (no build step for the core) and serves the UI from `web\dist`. To keep your
real config untouched while experimenting, set `FAIRLEAD_DATA_DIR` and `FAIRLEAD_PORT` first.

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
- The core is C# 5 (the .NET Framework 4.8 compiler behind `Add-Type`): no `?.`, `=>` members, `$""`, `nameof`,
  property initializers, `is T x` patterns, `when` filters, `throw` expressions or `await` in `catch`. Use
  `token.At("name")` for a null-safe JSON lookup. `Fairlead.Core.csproj` (LangVersion 5, warnings are errors) and CI's
  Add-Type check enforce it.
- Framework assemblies only (`System`, `System.Core`, `System.Net.Http`, `System.Security`): no NuGet packages, no
  third-party DLLs, no `Assembly.Load*`. Core `.cs` files are ASCII (Pester checks); write `"\u2026"`, not the
  character.
- Only `Providers/UpstreamClient.cs` makes outbound HTTP calls (Pester rule).
- The browser never sees an API key, a key hash or the DPAPI blob; tests assert it.
- Never show invented numbers: unknown prices, tokens and latencies render as "—".
- Nothing organisation-specific in the repository: no real hostnames, keys or team names.
- Commit messages and PR bodies carry no tool attribution lines.

## CI

`.github/workflows/ci.yml` runs on the self-hosted Windows runner (`self-hosted, Windows, X64`): core build and
tests on `net8.0` and `net48`, the core compiled by Windows PowerShell 5.1 `Add-Type` (`Test-FairleadAddType.ps1`),
UI typecheck/tests/build, Pester on Windows PowerShell 5.1, and uploads the bundle after checking it holds the core
source and no DLL.
