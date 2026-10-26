# Roadmap

## Done in 0.1

- OpenAI Chat Completions and Anthropic Messages in, either dialect out, streaming, tools and images, with
  same-dialect relays left untouched.
- Routes (failover, fastest, round robin), the auto route with editable rules, aliases for hard-coded model names,
  `provider/model` and bare served-model names.
- Kedge mode: Windows network events, TCP probes with hysteresis, passive signals from real calls, forced on/off.
- Circuit breakers per provider and per provider/model, Retry-After, parking, hedged requests, local fallback that
  prefers models already loaded in Ollama.
- Privacy routing: secret detection and the privacy header keep requests local.
- Client keys (hashed, shown once, route limits), CORS allow-list, LAN bind for containers, DPAPI-protected API keys.
- Browser setup (local detection, cloud keys, route suggestions, first key and snippets), overview, providers, routes and
  rules editors, clients, playground with dry-run explain, activity with decision traces, diagnostics, settings.
- Tray icon (Open, copy URL, Kedge mode, Exit), start-with-Windows shortcut, single instance, Stop script.

## Done after 0.1 (unreleased)

- The core ships as C# 5 source and is compiled in memory by Windows PowerShell 5.1 at launch: no DLL is shipped
  or loaded, and Newtonsoft.Json is replaced by Fairlead's own JSON model (same output, pinned by parity tests).

## Hackathon worklist

Pick one, put your name on it in the issue tracker, and tick its checklist under `integrations/`.

1. **Run every integration for real**: Claude Code, Open WebUI, Continue, Aider, LangChain, Halyard. Each folder
   has the recipe and a checklist; most fixes will be small translation gaps.
2. **Windlass**: Windlass v0.2 (C#, same PC) serves the contract in `integrations/windlass`; run it with a real model,
   register it as `deep-local` and tick that checklist.
3. **Semantic auto-routing**: score prompt difficulty with a small local embedding model (or a tiny local LLM as a
   judge) and add it as a rule condition. Must stay under ~50 ms per request; cache by prompt hash.
4. **Budgets**: per client key token or dollar limits per day, enforced in `GatewayEngine` before the attempt, with
   a 429 in the caller's dialect and a soft limit that downgrades `smart` to `fast`.
5. **Response cache**: exact-match cache for non-streaming, temperature-0 requests (hash of the canonical body),
   with a TTL and a Settings toggle. Semantic caching is a stretch goal.
6. **More inbound APIs**: the OpenAI Responses API (`/v1/responses`), legacy `/v1/completions` (some autocomplete
   plugins), and an Ollama-compatible `/api/chat` + `/api/tags` so Ollama-only tools can use Fairlead.
7. **More outbound dialects**: native Gemini (for features the compatible endpoint lacks), AWS Bedrock, Azure OpenAI
   (deployment-style URLs and `api-key` header).
8. **Carry Anthropic features across translation**: map extended thinking to `reasoning_content` and back, forward
   `anthropic-beta` headers to Anthropic targets, translate `response_format: json_schema` into a forced tool for
   Anthropic targets.
9. **Live view**: replace polling with server-sent events on `/api/events` for the overview and activity pages.
10. **MCP**: expose Fairlead's status, routes and a "ask model X" tool as an MCP server so agents can choose
    routes themselves.

## Later

- Replace the placeholder emblem with the detailed original (see `assets/brand/README.md`).
- Propagate `docs/UNIFIED-UI.md` 0.2 (Fairlead and Windlass rows, the starboard-green accent) and `fleet.css` 0.2 to
  every Fleet repo, as that document requires.
- Mid-stream failover for idempotent requests (buffer until the first token, then commit), if it proves worth the
  latency.
