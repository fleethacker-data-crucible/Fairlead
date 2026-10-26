# AI gateway (Responses API)

Fairlead can route to an AI gateway that serves the OpenAI Responses API (for example a Databricks AI Gateway).
The gateway's model file says which endpoints exist and which models each one serves. Fairlead turns every endpoint
into a provider, so tools keep calling Fairlead's usual OpenAI or Anthropic API with a route name (`auto`, `smart`,
your own routes) and never see a gateway host, token or provider model id.

## What lives where

| Thing | Where | In git? |
|---|---|---|
| Gateway model file (endpoints, paths, model ids) | `config\ai_gateway_models.yaml`, or the path in `FAIRLEAD_GATEWAY_CONFIG` | No (git-ignored). `config\ai_gateway_models.example.yaml` shows the format with made-up ids. |
| Gateway base URL | The environment variable the file names in `base_url_env` (usually `AI_GATEWAY_BASE_URL`) | No |
| Bearer token | The environment variable the file names in `headers.Authorization.token_env` (usually `DATABRICKS_TOKEN`) | No |
| Timeout | `AI_GATEWAY_TIMEOUT_SECONDS` (5 to 3600, default 120) | No |
| Routes such as `fast`, `balanced`, `reasoning` | Fairlead → Routes, targets like `mlflow_responses/<model id>` | No (`fairlead.json`) |

Set the two variables in your Windows user environment or through your deployment's secret injection. They can also
go in `config\fairlead.env` (git-ignored), but that file is plain text.

## Set it up

1. Put the gateway's model file at `config\ai_gateway_models.yaml` (or set `FAIRLEAD_GATEWAY_CONFIG`).
2. Set the variables it names.
3. Check it without starting Fairlead:

   ```powershell
   Import-Module .\module\Fairlead
   Test-FairleadModelConfig
   ```

   It lists endpoints, model counts, the variables the file needs and every problem, by key and variable name only.
   It never prints a URL, token or model id.
4. Start Fairlead. The console says how many gateway endpoints and models were added. Under Providers, each endpoint
   shows as a read-only provider.
5. Under Routes, make the logical routes your tools use (for example `fast`, `balanced`, `reasoning`) with gateway
   models as targets, in the order to try them.

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

- `Environment variable DATABRICKS_TOKEN (endpoints.mlflow_responses.headers.Authorization.token_env) is not set.`
- `models.example_fast.endpoint refers to 'mlflow', which is not under endpoints.`
- `endpoints.openai_responses.request_format 'x' is not supported; Fairlead speaks openai_responses_v1.`

Sections other than `environment`, `endpoints` and `models` are reported once and ignored.

## Later: Lazarette

The base URL and token are planned to come from the Lazarette vault instead of environment variables, with the model
file imported into Lazarette and shared from there. See [VISION](VISION.md).
