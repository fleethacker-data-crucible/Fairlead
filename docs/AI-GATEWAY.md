# AI gateway (Responses API)

Fairlead can route to an AI gateway that serves the OpenAI Responses API (for example a Databricks AI Gateway).
The gateway's model file says which endpoints exist and which models each one serves. Fairlead turns every endpoint
into a provider, so tools keep calling Fairlead's usual OpenAI or Anthropic API with a route name (`auto`, `smart`,
your own routes) and never see a gateway host, token or provider model id.

## What lives where

| Thing | Where | In git? |
|---|---|---|
| Gateway model file (endpoints, paths, model ids) | Imported under **Providers → AI gateway**; kept DPAPI-encrypted for your Windows account in Fairlead's data folder (`%LOCALAPPDATA%\Fairlead\ai-gateway-models.dpapi`). Also read from `FAIRLEAD_GATEWAY_CONFIG` (a path) or `config\ai_gateway_models.yaml` for scripted setups. | No |
| Endpoint address | Either the full `url:` of each endpoint in the model file (kept encrypted with it once imported; nothing else to enter), or `base_url_env` + `path` below. |
| Gateway base URL (the variable in `base_url_env`, usually `AI_GATEWAY_BASE_URL`) | Entered once under **Providers → AI gateway**; kept DPAPI-encrypted in `ai-gateway-values.json` in the data folder. The environment variable of that name still works and is used when nothing is entered. | No |
| Bearer token (the variable in `headers.Authorization.token_env`, usually `DATABRICKS_TOKEN`) | **Never stored by Fairlead.** It comes from Lazarette (the suite's encrypted vault; see below) or the environment variable of that name. | No |
| Timeout | `AI_GATEWAY_TIMEOUT_SECONDS` (5 to 3600, default 120) | No |
| Routes such as `fast`, `balanced`, `reasoning` | Fairlead → Routes, targets like `mlflow_responses/<model id>` | No (`fairlead.json`) |

The page never shows the address, the token or a model id back; it shows where each value comes from
(saved, environment, missing).

## Starting from the gateway's curl commands

`config/ai_gateway_models.template.yaml` is the gateway's documented curl commands (`model-curls.md`) as a model
file, in the same order: two endpoints and nine models. Its two `url:` values are placeholders
(`<OPENAI_RESPONSES_URL>`, `<MLFLOW_RESPONSES_URL>`). Fill them with the real endpoint addresses on a machine allowed
to see them, import the filled copy, and never commit it. A file that still holds a placeholder is refused with the
endpoint named. Fairlead then sends exactly what the curls send: `POST`, `Content-Type: application/json`,
`Authorization: Bearer <token>`, and `model`, `input` (user turns as `input_text`, assistant turns as `output_text`)
and `max_output_tokens` when the caller sets a limit.

## Several gateways: one model file each

Import one model file per gateway; Fairlead keeps them all (up to 16), each encrypted on its own under the name of
the file you picked. Importing a file with the same name replaces it; **Remove** on a file's row deletes only that
file. A file with problems adds nothing, and the others still load. Endpoint keys become provider ids, so a key may
appear in only one file.

`config/ai_gateway_chat_models.template.yaml` is the second gateway (`other-gemini-model.yaml`): an
OpenAI-compatible Chat Completions endpoint. Its endpoint says `request_format: "openai_chat_completions_v1"` and its
`url` is the endpoint's base address (or the full `.../chat/completions` address; both work). Fairlead sends the
OpenAI request as is, tool calls and streaming included, with `Authorization: Bearer <key>`. The key is
`AI_ENDPOINT_TOKEN`, from Lazarette or the environment. A chat file's `request_body.required_fields` lists `model`
and `messages`.

## Set it up

1. Open **Providers**. Under **AI gateway**, press **Import model file** and choose the gateway's YAML. A file with a
   wrong structure is refused with the reasons, and nothing is saved.
2. Enter the **gateway address** (`https://...`) and press **Save**.
3. Make the token available: share it from Lazarette (see [Lazarette](#lazarette) below), or set the environment variable the file names (for
   example `DATABRICKS_TOKEN`) in your Windows user environment and restart Fairlead.
4. The gateway endpoints appear as read-only providers at once, with no restart. **Import a new version** replaces the
   file; **Remove** deletes the file and the saved address.
5. Under Routes, make the logical routes your tools use (for example `fast`, `balanced`, `reasoning`) with gateway
   models as targets, in the order to try them.

Check a file without the browser:

```powershell
Import-Module .\module\Fairlead
Test-FairleadModelConfig
```

It lists endpoints, model counts, the variables the file needs and every problem, by key and variable name only. It
never prints a URL, token or model id.

## How a request is made

For a request routed to a gateway model, Fairlead:

1. resolves the route to a target `endpoint/model id` (the route's targets, in order);
2. builds the URL as the base URL from `base_url_env` without its trailing `/`, plus the endpoint's `path`;
3. sends `Authorization: Bearer <value of token_env>` and `Content-Type: application/json`;
4. sends `{ "model": <model_id>, "input": [ { "role", "content": [ { "type": "input_text", "text" } ] } ] }`, plus
   `max_output_tokens` only when the endpoint lists it under `optional_fields`. Nothing else from the file is sent;
5. reads the answer text from `output[].content[]` items of type `output_text` and returns it to the tool in the
   tool's own format. A tool that asked to stream gets the whole answer as one streamed message.

## Retries and fallback

- Transient failures (HTTP 5xx, 429, a timeout, a dropped connection) are retried on the same target up to 2 times,
  with exponential backoff and jitter (0.25 to 0.5 s, then 0.5 to 1 s). A `Retry-After` longer than the backoff ceiling
  skips the retries.
- HTTP 401 and 403 are never retried, nor are 400 and 404.
- After that, Fairlead moves to the route's next target. Every attempt, retry count and fallback is in the request's
  decision trace (Activity) and in the `X-Fairlead-Target` and `X-Fairlead-Attempts` response headers. A model is
  never changed silently.

## What is not assumed

Capability labels in the model file are treated as preferences, not facts. Until each is tested against the gateway,
gateway models are used for text only: requests with tools or images go to other targets, and Fairlead calls the
gateway without streaming. Embeddings are not routed to the gateway.

## When the file is wrong

Any error rejects the whole file: no gateway provider is added, Fairlead still starts, and the console, the log and the
Providers page say what to fix. Typical messages:

- `DATABRICKS_TOKEN (endpoints.mlflow_responses.headers.Authorization.token_env) is not set.`
- `models.example_fast.endpoint refers to 'mlflow', which is not under endpoints.`
- `endpoints.openai_responses.request_format 'x' is not supported; Fairlead speaks openai_responses_v1.`

Sections other than `environment`, `endpoints` and `models` are reported once and ignored.

## Lazarette

Tokens and API keys belong in Lazarette, the suite's encrypted vault. Fairlead never stores them.

1. In Lazarette: **Settings, Fairlead, Pair**. Copy the pairing code it shows once.
2. In Fairlead: **Providers, Lazarette**, paste the code, press **Pair**. The code is kept DPAPI-encrypted in
   Fairlead's data folder (`lazarette-pairing.dpapi`) and never shown again.
3. In Lazarette, share each secret with Fairlead under the name Fairlead asks for. The Lazarette card lists the
   names: every variable the gateway model file names that is not saved in Fairlead (the token), and the key variable
   of every provider without a key saved in Fairlead (for example `OPENAI_API_KEY`).

How it behaves:

- Fairlead asks only while Lazarette is open and unlocked, every 30 seconds (60 while it is locked or closed), and
  proves who it is with an HMAC over a fresh nonce; the pairing secret never crosses the wire. Lazarette's answer is
  sealed (AES-256-CBC + HMAC-SHA256) under a key only the pairing can derive; an answer that does not open is ignored.
  Contract: Lazarette `docs/FAIRLEAD.md`.
- Values are held in memory only. When Lazarette locks or closes, they are kept for the hold time under **Settings**
  (8 hours by default; "only while Lazarette is open" forgets them at once), counted from Lazarette's last
  confirmation. They are dropped at once when you remove the pairing in either app or stop sharing in Lazarette, and
  when Fairlead exits.
- Lazarette answers only the process that serves `/__fleethacker/id` on **port 8776** as `fairlead`. If Fairlead had to
  start on another port, the card says so; free 8776 and restart Fairlead.
- Key order for a provider: a key saved in Fairlead, then Lazarette, then the environment variable.
