# API

Fairlead has two HTTP APIs on the same loopback port (default `127.0.0.1:8776`):

- **Gateway** (`/v1`): what tools call. OpenAI and Anthropic dialects, authenticated with a client key.
- **Admin** (`/api`): every operator capability. The admin UI and the [command line](CLI.md) use nothing else, so a
  script can do anything they can. `GET /api` lists every admin route the running engine serves.

## Gateway (`/v1`)

Auth: `Authorization: Bearer fl-...` or `x-api-key: fl-...` (create keys with `fairlead clients add`). From this
machine a key is optional when `requireClientKey` is off. Other machines always need one.

| Route | |
|---|---|
| `POST /v1/chat/completions` | OpenAI chat; `model` is a route (`auto`, `smart`, `fast`, `local`), an alias, `provider/model`, or a served model name |
| `POST /v1/messages` | Anthropic messages, routed the same way |
| `POST /v1/messages/count_tokens` | Anthropic token estimate |
| `POST /v1/embeddings` | OpenAI embeddings |
| `POST /v1/audio/speech` | OpenAI speech: JSON `{ model, input, voice, ... }` in, audio out (the upstream's content type). Same client keys, routes, privacy header, failover and request log as chat (the request log never holds the text). Only OpenAI-compatible providers serve it; Anthropic, Ollama and Responses targets are skipped, so a private request has no target unless a local OpenAI-compatible server (for example a speech server on this machine) is on the route. `model` is a route, a `provider/model` or a served model name, as for chat |
| `POST /v1/audio/transcriptions` | OpenAI transcription: `multipart/form-data` (`file`, `model`, optional fields) in, JSON or text out. The file bytes are forwarded untouched; only `model` is rewritten to the target model. Same rules as speech |
| `GET /v1/models`, `GET /v1/models/{id}` | routes, then targets. An id that can serve audio carries `"audio": ["speech"]` and/or `["transcription"]`: a provider/model whose name says so (tts, speech, whisper, transcribe, stt) on an OpenAI-compatible provider, and a route with such a target. `GET /v1/models?capability=audio` lists only those |
| `GET /v1/health` | `{ ok, kedge, internet }`, no auth |

Request headers: `X-Fairlead-Route: <route>` forces a route; `X-Fairlead-Privacy: local` keeps the request on local
models. Response headers: `X-Fairlead-Route`, `-Target`, `-Mode` (`normal` or `kedge`), `-Attempts`, `-Request-Id`,
`-Private`.

## Admin (`/api`)

Auth: `X-Fairlead-Session: <token>`, plus a loopback `Host` and no foreign `Origin`.

- **The browser** gets the session token once, in the URL the launcher opens. It is never written to disk.
- **The CLI** reads a separate token from `%LOCALAPPDATA%\Fairlead\fairlead-cli.json`, encrypted with DPAPI for the
  user who started Fairlead (see [CLI.md](CLI.md)).

Errors are `{ "error": "...", "code": "...", "requestId": "..." }` with a 4xx/5xx status. Every write returns the new
config view, unless noted.

### Engine

| Route | Body | Returns |
|---|---|---|
| `GET /api` | | `{ version, routes: ["GET /api/status", ...] }` |
| `GET /api/health` | | `{ ok, version }` (no auth) |
| `GET /api/status` | | version, port, gateway URL, connectivity, providers with health, routes with target counts, metrics |
| `POST /api/heartbeat` | `{ leaving }` | browser presence, for auto-stop |
| `POST /api/session/stop` | header `X-Fairlead-Intent: end-session` | `{ stopping }` |

### Config, providers and models

| Route | Body |
|---|---|
| `GET /api/config` | config view: providers (key source and last four characters only), routes, rules, aliases, clients, settings |
| `GET /api/presets` | known providers |
| `GET /api/detect` | local model servers found on this machine |
| `POST /api/providers` | `{ provider: { id, name, kind, baseUrl, local, enabled, apiKeyEnv, timeoutSeconds, streamUsage, models, headers, retries }, apiKey?, clearKey? }`; creates or replaces; `apiKey` is stored DPAPI-encrypted |
| `DELETE /api/providers/{id}` | |
| `POST /api/providers/{id}/test` | returns `{ ok, ms, models, error, kind, keySource }` |
| `POST /api/providers/{id}/discover` | lists the provider's models and stores them as discovered |

### Routes, routing and clients

| Route | Body |
|---|---|
| `POST /api/routes` | `{ route: { id, description, strategy: failover\|fastest\|roundRobin\|auto, targets: ["provider/model"], hedgeAfterMs, localFallback }, previousId? }` |
| `DELETE /api/routes/{id}` | |
| `POST /api/routing` | `{ defaultRoute, autoFallbackRoute, rules: [{ name, route, enabled, when }], aliases: { glob: route } }` |
| `POST /api/routing/reset` | rules and aliases back to the defaults |
| `POST /api/explain` | `{ prompt \| body, model?, route?, private?, client?, withTools? }`; dry run: route, decision trace, candidates |
| `POST /api/playground` | same body, plus `system?`, `maxTokens?`; sends it through the gateway and returns `{ status, text, error, route, target, record }` |
| `POST /api/clients` | `{ name, routes? }`; returns `{ key }` once (only its hash is kept) |
| `DELETE /api/clients/{id}` | |

### Settings and Kedge mode

| Route | Body |
|---|---|
| `POST /api/settings` | any of `{ kedge, requireClientKey, setupComplete, lazaretteHoldHours, logContent, corsOrigins, privacy: {...}, network: {...} }` (`privacy` and `network` are whole objects) |
| `POST /api/kedge` | `{ mode: auto\|on\|off }`; returns `{ mode, connectivity }` |

### AI gateway model files and Lazarette

| Route | Body |
|---|---|
| `POST /api/gateway/import` | `{ yaml, fileName, values? }`; returns `{ saved, errors, warnings, gateway }` |
| `POST /api/gateway/curl` | `{ curl, tokenVariable? }`; builds a gateway from a pasted curl command |
| `POST /api/gateway/values` | `{ values: { NAME: value } }`; stored DPAPI-encrypted |
| `POST /api/gateway/test` | `{ target: "provider/model" }`; returns `{ ok, verdict, message, status, detail }` |
| `DELETE /api/gateway`, `DELETE /api/gateway/{name}` | every file and the saved address, or one file |
| `GET /api/lazarette` | pairing state and the names it holds (never values) |
| `POST /api/lazarette/pair` | `{ code }` |
| `DELETE /api/lazarette/pair` | |
| `POST /api/lazarette/refresh` | |

### Health and activity

| Route | |
|---|---|
| `GET /api/health/providers` | connectivity, per-provider probes and models, circuit breakers |
| `POST /api/probe` | probe the network and providers now |
| `GET /api/cache` | the response cache: enabled, lifetime, entries, hits and misses (off unless `FAIRLEAD_RESPONSE_CACHE_SECONDS` is set) |
| `POST /api/cache/clear` | forget every cached answer |
| `POST /api/breakers/reset` | `{ provider? }`; every breaker when omitted |
| `GET /api/requests?limit=N` | recent requests with their decision trace and attempts (newest first, up to 1000) |
| `GET /api/requests/{id}` | one request |
| `GET /api/bearing` | cited answers: whether `FAIRLEAD_BEARING_DIR` is set, files and passages indexed (see [BEARING.md](BEARING.md)) |
| `GET /api/bearing/search?q=&limit=` | the passages a question would retrieve, with file, lines, score and link (no model is called) |
| `GET /api/requestlog?since=&until=&client=&label=&model=&route=&status=&limit=` | the durable request log read from `logs\fairlead-requests-<date>.jsonl` (survives restarts): who (client key name, id and visible prefix, optional `X-Fairlead-Label`), what (endpoint, route, status, tokens, latency) and which model served it. `since` is `90m`, `24h`, `7d` or a date; `status` is `ok`, `error`, `4xx`, `5xx` or a code; newest first, default 100, up to 5000. Never holds prompt or response text |
| `GET /api/requestlog/summary` | the same filters, totalled per client, label, serving model and route (requests, failures, tokens, cost when priced, p50/p95 latency) |
| `GET /api/metrics` | totals, last hour, per-target stats |
| `GET /api/logs?level=` | recent diagnostics |
| `GET /api/live` | server-sent events (`start`, `attempt`, `firstByte`, `done`) for each request as it happens (engines with the live routing view) |
