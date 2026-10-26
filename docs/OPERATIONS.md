# Operations

## Starting and stopping

| Action | How |
|---|---|
| Start | `Launch-Fairlead.bat` (tray icon; the setup page opens on first run) |
| Open the admin page | Tray icon → **Open Fairlead** (double-click works too), or `Launch-Fairlead.bat --open` when not running |
| Start with Windows | `scripts\Register-FairleadStartup.ps1` (a shortcut in your Startup folder; `-Remove` undoes it) |
| Stop | Tray → **Exit Fairlead**, the **End session** button in the admin page, or `scripts\Stop-Fairlead.ps1` |
| Restart | `Launch-Fairlead.bat --replace` |
| Diagnose start-up | `Launch-Fairlead.bat --console --no-tray` keeps the window and Ctrl+C works |

Launcher options: `--open`, `--console`, `--no-tray`, `--no-browser` (prints the one-time admin link instead),
`--with-tab` (stop 20 s after the admin tab closes), `--replace`. `--stay` is accepted and is already the default.

Fairlead keeps running after the browser closes on purpose: other tools depend on it. That is the TechStack tray
exception; every other lifecycle rule applies (runtime file, single instance, Stop script, the build stops it).

## Files

| What | Where |
|---|---|
| Browser-made config (providers, routes, rules, aliases, client key hashes, DPAPI-protected API keys) | `%LOCALAPPDATA%\Fairlead\fairlead.json` (+ `.bak` of the previous save) |
| Process settings (port, bind, auto-stop, data folder, env-var keys) | `config\fairlead.env` (copy `fairlead.env.example`) |
| Runtime file (pid, port, gateway URL; never the session token) | `logs\fairlead-server-<pid>.json` |
| Engine log (redacted JSONL) | `logs\fairlead-<time>-<pid>.jsonl` |
| Request log (no prompt text, ever) | `logs\fairlead-requests-<date>.jsonl` |

`fairlead.json` moves between machines only for routes and rules: DPAPI keys can be read only by the Windows user
that saved them, so re-enter keys on a new machine (or use environment variables).

## The hackathon network

- Fairlead listens on this machine only (127.0.0.1, a Fleet rule), so tools must run on the same PC. Containers and other
  machines cannot reach it; `FAIRLEAD_BIND=lan` was removed and is refused at start.
- Browser apps calling `/v1` directly: list their origin under Settings → Browser origins.
- Teams on a budget: give each team its own key limited to `local,fast` (Clients page) and a rule on the client name.
- Conference Wi-Fi drops: leave Kedge on **auto**. For a demo that must not depend on the network, force **on**.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Tools get "connection refused" | Is the tray icon there? Did the port move (console, tray tooltip, red pill in the UI)? |
| 401 from `/v1` | The tool is not sending its `fl-` key (`Authorization: Bearer` or `x-api-key`). |
| 403 "Browser origin … is not allowed" | Add the origin under Settings. |
| Everything goes local | Kedge mode: Diagnostics shows the probe results; a corporate firewall may block the probe hosts (edit them in Settings). |
| A cloud provider is skipped | Diagnostics → circuit breakers: reset it after fixing the key or once the outage is over. |
| Empty answers from a local thinking model | The model spent `max_tokens` on reasoning; raise the limit or route to a non-thinking model. |
| Ollama answers slowly the first time | Model load. Fairlead prefers models already in memory when it picks one itself. |
| Long prompts look truncated on Ollama | Ollama's own context default (`num_ctx`) is smaller than the model's maximum; raise it in a Modelfile. |
