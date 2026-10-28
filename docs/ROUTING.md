# Routing and Kedge mode

## How a model name becomes a target

A request names a model. Fairlead turns that into an ordered list of targets (`provider/model`) like this, first
match wins:

1. **`X-Fairlead-Route` header**: a route by name, whatever the model says.
2. **A route name**: `auto`, `smart`, `fast`, `local`, or any route you create (`fairlead/smart` also works).
3. **`provider/model`**: that exact model, e.g. `ollama/qwen2.5:14b`, `openrouter/anthropic/claude-x`.
4. **An alias**: glob patterns for names tools hard-code (`*haiku*` → fast, `claude-*` → smart). An exact alias
   wins; otherwise the first matching pattern from the top.
5. **A bare model a provider serves**: `qwen2.5:14b` goes to the provider that lists it.
6. **The default route** (`auto` unless changed).

## The auto route

`auto` has no targets. It checks the rules top to bottom; the first rule whose conditions all hold names the route.
Conditions: private (secrets or the privacy header), estimated prompt tokens at least / at most, uses tools,
has images, any keyword in the latest user message, and the calling client key's name. Defaults:

| Rule | When | Route |
|---|---|---|
| private-stays-local | secrets found or `X-Fairlead-Privacy: local` | local |
| long-context | ≥ 24,000 tokens | smart |
| agentic-tools | the request defines tools | smart |
| images | the request carries images | smart |
| deep-reasoning | "step by step", "debug", "refactor", "root cause", … | smart |
| quick-chat | ≤ 1,500 tokens | fast |
| (no match) | | smart (the auto fallback route) |

Token counts are estimates (characters / 4 plus tool schemas), good enough to pick a context window.

## From a route to an attempt order

1. **Filters**: Kedge mode or a private request keep local providers only. A model known to lack tools or image
   input, or whose context window is too small for prompt + `max_tokens`, is skipped with a reason. Unknown
   capabilities never exclude a model.
2. **Order** by the route's strategy: *failover* (as listed), *fastest* (lowest measured time to first token,
   unmeasured last), *round robin*.
3. **Parking**: a target whose breaker is open, whose probe failed or whose key was rejected moves to the end. It is
   still tried as a last resort rather than failing the request outright.
4. **Fallback**: when nothing healthy is left and the route allows it, the `local` route's targets are added; if
   that is empty too, Fairlead picks a model from a reachable local server itself, preferring one already loaded in
   memory (Ollama `/api/ps`) and one with tool support when the request has tools.

Every step is recorded in plain words; the Playground's **Explain** shows it without calling anything, and each
Activity entry keeps it.

## Failures

| Upstream result | Kind | Breaker | Next target? |
|---|---|---|---|
| DNS failure, refused, unreachable | network | opens at once, nudges the health monitor | yes |
| No headers within the provider timeout | timeout | counts toward 3 | yes |
| 5xx, 529 | server | counts toward 3 | yes |
| 429 | rate limited | open for `Retry-After` (default 30 s) | yes |
| 401, 403 | auth | open for an hour or until the key changes | yes |
| 404, "model not found" | model not found | the provider/model breaker, 5 min | yes |
| 400, 422 | bad request | untouched | **no**: the request is wrong, not the target |

Breakers start closed, open after three consecutive failures (one for network errors), stay open 10 s doubling to
5 min on repeated failure, then half-open: exactly one trial call decides. A passing deep probe closes them.

**Hedging** (`hedgeAfterMs` on a route): when the first target has not sent headers after that many milliseconds,
the next target starts too; whichever answers first wins and the other is cancelled. It costs a duplicate call
when it fires, so use it on `fast` routes with cheap targets.

## Kedge mode and health

The health monitor runs on its own:

- Subscribes to Windows `NetworkAvailabilityChanged` and `NetworkAddressChanged`; losing every adapter switches to
  Kedge mode immediately.
- Every 15 s (5 s while offline) it opens TCP connections to the probe hosts (`1.1.1.1:443`, `8.8.8.8:443`,
  `9.9.9.9:443`) **and** every cloud provider's own host. Any success means the internet is up, so networks that
  block public resolvers but allow the APIs still count as online.
- Two failed rounds in a row mean down, two good rounds mean up (no flapping). A real cloud call that succeeds
  counts as up at once; a real cloud call that fails at the network level triggers a probe at once.
- Local servers are asked for their model list every round, which keeps discovery fresh as models are pulled.
- Cloud keys are verified with the free model-list call at start, after any config change and every 10 minutes.

Kedge setting: **auto** (local only while the internet is down), **on** (local only, always), **off** (never switch on
its own; routes still fall back per request). Tray, Settings page and `POST /api/kedge` all set it. Responses carry
`X-Fairlead-Mode: kedge|normal`.

## Headers

| Header | Direction | Meaning |
|---|---|---|
| `X-Fairlead-Route` | request | Use this route whatever the model name says |
| `X-Fairlead-Privacy: local` | request | Treat as private: local models only |
| `X-Fairlead-Route` | response | Route that served the request |
| `X-Fairlead-Target` | response | `provider/model` that answered |
| `X-Fairlead-Mode` | response | `kedge` or `normal` |
| `X-Fairlead-Attempts` | response | Targets tried (including a lost hedge race) |
| `X-Fairlead-Request-Id` | response | Key into the Activity log |
| `X-Fairlead-Private` | response | `1` when the request was kept local as private |
| `X-Fairlead-Fallback` | response | Set only when the answer came from outside the route asked for: `local` (the route's local fallback) or `discovered` (a model found on a local server). `X-Fairlead-Target` then names that model, not the one asked for, and clients should credit it |
| `X-Fairlead-Fallback-Reason` | response | Why: the routing decision, e.g. "Every target of 'openrouter/…' is down; falling back to 'local'." |
