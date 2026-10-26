# AI gateway (Responses API)

Fairlead can route to an AI gateway that serves the OpenAI Responses API (for example a Databricks AI Gateway).
The gateway's model file says which endpoints exist and which models each one serves. Fairlead turns every endpoint
into a provider, so tools keep calling Fairlead's usual OpenAI or Anthropic API with a route name (`auto`, `smart`,
your own routes) and never see a gateway host, token or provider model id.

## What lives where

| Thing | Where | In git? |
|---|---|---|
| Gateway model file (endpoints, paths, model ids) | Imported under **Providers → AI gateway**; kept DPAPI-encrypted for your Windows account in Fairlead's data folder (`%LOCALAPPDATA%\Fairlead\ai-gateway-models.dpapi`). Also read from `FAIRLEAD_GATEWAY_CONFIG` (a path) or `config\ai_gateway_models.yaml` for scripted setups. | No |
| Gateway base URL (the variable in `base_url_env`, usually `AI_GATEWAY_BASE_URL`) | Entered once under **Providers → AI gateway**; kept DPAPI-encrypted in `ai-gateway-values.json` in the data folder. The environment variable of that name still works and is used when nothing is entered. | No |
| Bearer token (the variable in `headers.Authorization.token_env`, usually `DATABRICKS_TOKEN`) | **Never stored by Fairlead.** It comes from Lazarette (the suite's encrypted vault; connection in progress) or, until then, the environment variable of that name. | No |
| Timeout | `AI_GATEWAY_TIMEOUT_SECONDS` (5 to 3600, default 120) | No |
| Routes such as `fast`, `balanced`, `reasoning` | Fairlead → Routes, targets like `mlflow_responses/<model id>` | No (`fairlead.json`) |

The page never shows the address, the token or a model id back; it shows where each value comes from
(saved, environment, missing).

## Set it up

1. Open **Providers**. Under **AI gateway**, press **Import model file** and choose the gateway's YAML. A file with a
   wrong structure is refused with the reasons, and nothing is saved.
2. Enter the **gateway address** (`https://...`) and press **Save**.
3. Make the token available: stow it in Lazarette, or for now set the environment variable the file names (for
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

Tokens belong in Lazarette, the suite's encrypted vault. Fairlead asks Lazarette for the token by its variable name
once you have connected the two and granted Fairlead access in Lazarette; Fairlead holds it in memory only. Until that
connection ships, use the environment variable.
