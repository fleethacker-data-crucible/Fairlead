# Vision: the Fleet's intelligence router

Status: direction recorded 2026-10-26. Docs only; no code, defaults or config have changed yet. Fairlead 0.1 (the
model router described in the [README](../README.md) and [Architecture](ARCHITECTURE.md)) is the starting point, not
something this replaces.

## The direction

Fairlead becomes the **intelligence router** for every Fleet tool. Tools stop carrying their own external endpoints
and default to Fairlead for three kinds of access:

| Access | Today (in each tool) | With the router |
| --- | --- | --- |
| **AI / LLM endpoints** | Each tool has its own URL, model and token settings, and some hard-code an external default (for example `https://api.genai.mil/v1`, `https://api.openai.com/v1/chat/completions`) | Tools call Fairlead's OpenAI/Anthropic API on loopback with a route (`auto`, `smart`, `fast`, `local`). Fairlead 0.1 already does this part. |
| **Git repositories** | Tools call GitHub/GitLab REST and GraphQL themselves, with hard-coded hosts (`https://api.github.com`, `https://web.git.mil`) and their own tokens | Tools reach repositories through Fairlead, which knows the hosts, holds the credentials and applies policy (new scope) |
| **Databases** | Not wired yet; Semaphore models data sources and names their secrets by Lazarette vault item | Tools get connections through Fairlead instead of carrying connection details (new scope) |

What Fairlead owns:

- **Endpoint selection:** which target serves a request. For models, that means local phoenix-lai at
  `http://127.0.0.1:1234/v1` versus a remote provider such as genai.mil, plus Kedge mode when the network drops.
  For git and databases, it means which host or server.
- **Credentials:** kept in Lazarette or Windows Credential Manager and used by Fairlead, never stored in a tool's
  config or env file. Tools hold at most a Fairlead client key.
- **Policy:** allowed hosts, privacy routing (secrets stay local), which tool may reach which target.
- **Routing:** failover, health, and the decision trace for every request.

What tools keep: **no hard-coded external defaults.** A tool's default is the router; an explicit override stays
possible during migration. Until a tool is moved, its current settings keep working unchanged. Each tool's
`TechStack` has an "Intelligence router" section saying what it uses today.

## Constraints

The router follows the same [TechStack](../TechStack) rules as every Fleet tool:

- Windows PowerShell 5.1 on the workstation, serving only on loopback (`127.0.0.1`). No admin, no installers, no
  services.
- C# ships as source and is compiled at start by PowerShell 5.1 `Add-Type` (C# 5), using framework assemblies only.
  The workstation's application control blocks prebuilt and custom DLLs (confirmed 2026-10-26).
- No third-party DLLs or NuGet packages. Build off the workstation; bundle everything; no CDNs or runtime downloads.

Fairlead 0.1 does not meet the DLL rule yet; see the first open question.

## Migration inventory

These are the consumers found on 2026-10-26 across the Fleet tools in scope. Line numbers are from each repo's
`main` on that date. **Hard-coded external defaults are in bold.**

| Tool | Uses today | Where | Settings |
| --- | --- | --- | --- |
| Halyard | AI: OpenAI-compatible, **default `https://api.openai.com/v1/chat/completions`**; Databricks profile (no default) | `src/Halyard.Core/Llm/LlmClient.cs:39`; `Config/HalyardSettings.cs:135-145` | `HALYARD_LLM_PROFILE`, `LLM_URL`/`OPENAI_BASE_URL`, `LLM_MODEL`, `LLM_TOKEN`/`OPENAI_API_KEY`, `DATABRICKS_*`; vault `HALYARD_LAZARETTE_ITEM_Llm`/`_Databricks` |
| Halyard | Git: GitHub REST + GraphQL, **default `https://api.github.com`**; GitLab REST (no default) | `src/Halyard.Core/Providers/ProviderSettings.cs:44`; `HalyardSettings.cs:99-116` | `HALYARD_PROVIDER`, `GITHUB_API_URL`, `GITHUB_TOKEN`/`GH_TOKEN`, `GITLAB_URL`, `GITLAB_TOKEN`, `HALYARD_SOURCE_REPO`, `HALYARD_SANDBOX_REPO` |
| Gangplank | AI: OpenAI-compatible (openai, azure, databricks, ollama), **default `https://api.genai.mil/v1`**, always allowlisted | `Core/Intelligence.ps1:17` (allowlist `:43-58`) | `GANGPLANK_AI_URL`/`LLM_URL`/`OPENAI_BASE_URL`, `GANGPLANK_AI_MODEL`, `GANGPLANK_AI_ALLOWED_HOSTS`, `GANGPLANK_AI_TOKEN`/`GENAI_TOKEN`/`LLM_TOKEN`/`OPENAI_API_KEY`; vault `genapi` |
| Gangplank | Git: GitHub REST, **default `https://api.github.com`**; GitLab REST, **default `https://web.git.mil`**; archive sync, commits-API push | `Providers/GitProviderBase.ps1:67`, `:71`; `Core/Workspace.ps1:12` | `GITHUB_TOKEN`, `GITLAB_TOKEN`, `GANGPLANK_GITLAB_ALLOWED_HOSTS`; vault `github`, `gitmil` |
| Bowerbird | AI: OpenAI-compatible, default local phoenix-lai `http://127.0.0.1:1234/v1/chat/completions`; Grok profile (no default) | `Private/BowerbirdLlm.ps1:10` | `LLM_URL`, `BOWERBIRD_AI_ENDPOINT_URL`, `BOWERBIRD_GROK_URL`, `LLM_TOKEN`, `AI_ENDPOINT_TOKEN`/`GENAI_TOKEN`, `DATABRICKS_TOKEN`; vault `ai-endpoint-api`, `grok-api` |
| PacketRat | AI: OpenAI-compatible, default local phoenix-lai `http://127.0.0.1:1234/v1/chat/completions`; Grok profile (no default) | `Private/PacketRatLlm.ps1:12` | `LLM_URL`, `PACKETRAT_AI_ENDPOINT_URL`, `PACKETRAT_GROK_URL`, `PACKETRAT_LLM_PROFILE`, `LLM_TOKEN`, `AI_ENDPOINT_TOKEN`/`GENAI_TOKEN`, `DATABRICKS_TOKEN`; vault `ai-endpoint-api`, `grok-api` |
| Eight Bells | AI: OpenAI-compatible, entered by hand, no default, in memory only | `Start-EightBells.ps1:131` | UI Settings → "AI provider · Manual" (endpoint, model, key) |
| Lazarette | AI: Stark assistant, OpenAI-compatible, default placeholder `https://ai.example.com` | `lib/core/Config.ps1:131-133`; `lib/server/Stark.ps1:77` | `StarkBaseUrl`, `StarkModel`, `StarkKeyItem`; also the credential store other tools (and the router) read |
| Semaphore | AI: OpenAI-compatible and contract providers, caller-supplied, no default | `src/core/intelligence/remote.ts:23-32` | `RemoteSettings.url`/`.model`/`.token` |
| Semaphore | Databases: data-source catalog (postgres, oracle, access, tsql, ansi; Athena, Redshift, Azure SQL, Foundry), secrets by vault item, no driver code yet | `src/core/catalog.ts:5-23` | `vaultItem` per source |
| Shellback | AI: local rules by default; optional remote OpenAI-compatible or contract provider from the browser; loopback adapter | `src/storage/intel-settings.ts:20`; `tools/intel-adapter.mjs:24-28` | UI Program → Intelligence, `SHELLBACK_INTEL_HOST`; adapter `INTEL_UPSTREAM_URL`, `INTEL_API_KEY`, `INTEL_MODEL` |

No AI, git or database access was found in Capstan, Scrimshaw, SnapShackle or Halyard-Ballast (the sandbox
repository Halyard writes to; it has no code). The only network calls in those tools are loopback, or in Scrimshaw
the LMS reporting of a published course. CI workflows that call `api.github.com` were left out; they run on the
runner, not the workstation.

## Open questions

These are deliberately undecided.

1. **Fairlead's own build versus the DLL rule.** Settled (2026-10-26): the core is C# 5 source compiled by
   `Add-Type` at start, with a built-in JSON library in place of Newtonsoft.Json; no DLL ships, so nothing needs
   signing.
2. **Git access model.** Should Fairlead proxy GitHub/GitLab API calls, act as a credential broker that hands tools a
   short-lived token, or both? Archive download and push through the commits API (Gangplank) are large transfers.
   Should they pass through the router?
3. **Database access model.** Should the router proxy queries or hand out connection details? With which drivers,
   given framework assemblies only (for example `System.Data` OLE DB/ODBC versus none)? How do results stream back
   over loopback?
4. **Tool-facing API.** Fairlead speaks OpenAI and Anthropic for models today. What is the contract for git and data
   requests? Is it one API or separate endpoints? Are they versioned?
5. **Tool identity.** Do Fleet tools use Fairlead client keys (`fl-…`) like external tools? How is a tool's key
   provisioned without putting it in the tool's config?
6. **Credential source of truth.** Fairlead 0.1 stores provider keys with DPAPI in its own config. Should they move
   to Lazarette, to Windows Credential Manager, or should both be supported?
7. **Defaults and allowlists.** Which external hosts may the router reach by default (genai.mil, github.com,
   web.git.mil), and who changes that list? Gangplank currently always allows `api.genai.mil`.
8. **When the router is not running.** Should tools fail closed, fall back to their current settings, or start
   Fairlead themselves? Fairlead runs as a tray app that outlives tabs (the TechStack tray exception).
9. **Port and discovery.** Fairlead uses registry port 8776. How do tools find it if that port is taken? Does the
   edge-ops port registry change?
10. **Order of migration.** Do the tools with hard-coded external defaults (Gangplank, Halyard) go first, or the
    tools already defaulting to local phoenix-lai (Bowerbird, PacketRat)?
