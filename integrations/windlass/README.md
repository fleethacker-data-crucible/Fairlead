# Windlass (very large local models)

**Status:** Hackathon. Windlass is a separate program and repository (Python, GPU), built to this contract.

## What Windlass is

A layer-by-layer inference server (the AirLLM approach) that runs 70B+ models on a small GPU by loading one
transformer layer at a time from disk. It is slow (seconds to minutes per token) but can run models nothing else on
the machine can. Fairlead treats it as one more local, OpenAI-compatible provider. Fairlead needs no code for it.

## Contract Windlass serves

- `POST /v1/chat/completions`, OpenAI format, `stream: false` and `stream: true` (SSE chunks, then `data: [DONE]`).
  Honour `stream_options.include_usage` with a final `usage` chunk; return `usage` on non-stream responses.
- `GET /v1/models` → `{ "object": "list", "data": [{ "id": "...", "context_length": N }] }`.
- Errors as `{ "error": { "message", "type" } }` with real status codes: 404 unknown model, 503 busy or loading
  (Fairlead fails over on 5xx, 429 and timeouts; it does not retry 400).
- When streaming, send headers as soon as the request is accepted, and an SSE comment (`: keepalive`) at least every
  60 s while a layer pass runs: Fairlead cuts a stream that stays silent longer than the provider timeout.
- Default bind `127.0.0.1:8777` (Fleet registry port, next after Fairlead's 8776); a LAN option for a GPU box.

## Registering it in Fairlead

1. Providers → Add → **Custom (OpenAI-compatible)**. Base URL `http://127.0.0.1:8777/v1` (or the GPU box),
   **Local** ticked, timeout up to 3600 s, then **Fetch model list**.
2. Routes → New route `deep-local`, strategy Failover, target `windlass/<model>`, local fallback off.
3. Never put it in `auto`, `fast` or `local`: interactive tools would wait minutes. Reach it by name
   (`model: "deep-local"`) or with a rule on a client key used for batch jobs.

## Checklist

- [ ] `GET /v1/models` lists the model and its context length
- [ ] A short non-stream request completes inside the timeout
- [ ] A streamed request survives a multi-minute layer pass (keepalives arrive)
- [ ] Fairlead's Activity shows the tokens and the long first-token time
- [ ] A second request while busy gets 503 (or queues) and Fairlead reports it clearly
