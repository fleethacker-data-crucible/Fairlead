# Command line

`fairlead.cmd` (in the repository root) does everything the admin UI does, from a terminal or a script. Each command
calls one route of the [admin API](API.md), the same API the browser uses, so the CLI, the UI and your own scripts
always agree. Nothing to install: it is Windows PowerShell 5.1 (`scripts\fairlead.ps1`).

```powershell
.\fairlead.cmd help
.\fairlead.cmd status
.\fairlead.cmd routes set smart --targets openai/gpt-5-mini,ollama/qwen2.5:14b
.\fairlead.cmd chat "Say hello" --model auto
```

Put the repository folder on `PATH` to type `fairlead` from anywhere.

## How it reaches the engine

While Fairlead runs, it writes `%LOCALAPPDATA%\Fairlead\fairlead-cli.json`: the port, the process id and a CLI token
encrypted with Windows DPAPI for the user who started Fairlead. The CLI decrypts it and sends the token in the
`X-Fairlead-Session` header. The file is removed when the engine stops.

- **Other Windows accounts** on the machine cannot decrypt the token.
- **Programs running as you** can, but they can already read your config and stored keys.
- **The browser's session token** never touches disk; the CLI token is a different value.
- **Another local engine** (another data folder, or a loopback SSH tunnel): pass `--url` or set `FAIRLEAD_URL`, and supply the session through `FAIRLEAD_SESSION`. Session tokens are refused on the command line. URLs must be loopback HTTP(S) origins, without credentials, path, query or fragment, and HTTP redirects are refused. `FAIRLEAD_DATA_DIR` points the CLI at the same data folder the engine uses.

## Commands

| Command | API route |
|---|---|
| `start [--console] [--no-tray] [--open] [--replace]` | runs `scripts\Start-Fairlead.ps1` |
| `stop --yes` | `POST /api/session/stop` |
| `startup on\|off` | runs `scripts\Register-FairleadStartup.ps1` |
| `status` | `GET /api/status` |
| `api` (every route) | `GET /api` |
| `config` | `GET /api/config` |
| `providers [list]`, `providers show <id>` | `GET /api/config` |
| `providers set <id> [--preset P] [--name] [--kind] [--base-url] [--local] [--enabled] [--timeout] [--key-env] [--retries] [--key-stdin\|--clear-key]` | `POST /api/providers` |
| `providers key <id>` (hidden prompt or stdin) | `POST /api/providers` |
| `providers enable\|disable <id>` | `POST /api/providers` |
| `providers remove <id> --yes` | `DELETE /api/providers/{id}` |
| `providers test <id>` | `POST /api/providers/{id}/test` |
| `providers discover <id>` | `POST /api/providers/{id}/discover` |
| `presets` | `GET /api/presets` |
| `detect` | `GET /api/detect` |
| `routes [list]`, `routes show <id>` | `GET /api/config` |
| `routes set <id> [--targets a,b] [--add-target] [--remove-target] [--strategy] [--hedge-ms] [--local-fallback] [--description] [--rename]` | `POST /api/routes` |
| `routes remove <id> --yes` | `DELETE /api/routes/{id}` |
| `routing [show]` | `GET /api/config` |
| `routing set [--default R] [--auto-fallback R] [--file routing.json]` | `POST /api/routing` |
| `routing reset --yes` | `POST /api/routing/reset` |
| `explain <prompt> [--model] [--route] [--client] [--private] [--tools]` | `POST /api/explain` |
| `chat <prompt> [--model] [--route] [--system] [--max-tokens] [--tools] [--private]` | `POST /api/playground` |
| `clients [list]` | `GET /api/config` |
| `clients add <name> [--routes a,b] [--rate N]` (prints the key once) | `POST /api/clients` |
| `clients rate <id\|name> <N\|off>` | `POST /api/clients/{id}/rate-limit` |
| `clients remove <id\|name> --yes` | `DELETE /api/clients/{id}` |
| `kedge [auto\|on\|off]` | `POST /api/kedge` |
| `settings [show]`, `settings set key=value ...` | `POST /api/settings` |
| `gateway [show]` | `GET /api/status` |
| `gateway import <file.yaml>` | `POST /api/gateway/import` |
| `gateway curl "<curl command>" [--token-variable NAME]` | `POST /api/gateway/curl` |
| `gateway value <NAME>` (hidden prompt or stdin) | `POST /api/gateway/values` |
| `gateway test <provider/model>` | `POST /api/gateway/test` |
| `gateway remove [name] --yes` | `DELETE /api/gateway[/{name}]` |
| `lazarette [status]`, `pair <code>`, `unpair --yes`, `refresh` | `/api/lazarette...` |
| `health` | `GET /api/health/providers` |
| `probe` | `POST /api/probe` |
| `cache [status]`, `cache clear --yes` | `GET /api/cache`, `POST /api/cache/clear` |
| `breakers`, `breakers reset [provider]` | `GET /api/health/providers`, `POST /api/breakers/reset` |
| `requests [--limit N]`, `request <id>` | `GET /api/requests[/{id}]` |
| `bearing [status]`, `bearing search <question> [--limit N]` | `GET /api/bearing[/search]` |
| `requestlog [--since 24h] [--client N] [--label L] [--model M] [--route R] [--status S] [--limit N] [--summary]` | `GET /api/requestlog[/summary]` |
| `commands [--json]` | none (offline): every command with usage, routes and whether it needs `--yes` |
| `metrics` | `GET /api/metrics` |
| `logs [--level info\|warn\|error]` | `GET /api/logs` |
| `live [--count N]` | `GET /api/live` (server-sent events) |

`settings set` keys: `requireClientKey`, `logContent`, `setupComplete`, `kedge`, `lazaretteHoldHours`, `corsOrigins`
(comma-separated), `privacy.detectSecrets`, `privacy.forceLocalWhenSensitive`, and `network.<field>` (see
`fairlead settings`).

## For scripts

- `--json` prints the value as JSON on stdout instead of a table.
- Errors go to stderr as `fairlead: <message>`, with exit code 1. `providers test`, `gateway test` and `chat` also exit
  1 when the check or request fails.
- Destructive commands ask for confirmation; from a script they need `--yes`.
- Secrets never go on the command line. Pipe them in:
  `$key | fairlead providers key openai`, or `fairlead providers set openai --preset openai --key-stdin`.

```powershell
# Fail a CI job when a route has nowhere to go
$routes = .\fairlead.cmd routes --json | ConvertFrom-Json
$empty = $routes | Where-Object { $_.strategy -ne 'auto' -and $_.targets.Count -eq 0 }
if ($empty) { throw "Routes with no targets: $($empty.id -join ', ')" }
```
