# Claude Code

**Status:** Hackathon. The wire format it uses is verified (Messages API, streaming, tools, `count_tokens`); the CLI
itself has not been run end to end through Fairlead yet.

## Setup

```powershell
$env:ANTHROPIC_BASE_URL = "http://127.0.0.1:8776"
$env:ANTHROPIC_AUTH_TOKEN = "fl-YOUR-KEY"
claude
```

Claude Code sends Claude model ids. Fairlead's aliases map `*haiku*` to `fast` and `claude-*` to `smart`, so with a
Claude key configured it reaches Claude, and in Kedge mode it lands on your local model with no change on the
Claude Code side. To pin a route regardless of the model name, point `ANTHROPIC_MODEL` at a route name such as
`smart` or `local`.

## Checklist

- [x] Chat answers, streaming visible
- [ ] File edits and bash tools work (tool_use / tool_result round trips) on Claude
- [ ] Same on a local model in Kedge mode (expect weaker tool use from small models: prefer a tools-capable model in `local`)
- [ ] `/cost` or the status line still works with the counted tokens (Fairlead estimates `count_tokens`)
- [ ] Long sessions: no truncation surprises with the local model's context window

Run 2026-10-27 (Claude Code 2.1.312, headless `claude -p`, translated to Ollama `qwen3.8-abliterated`, outside Kedge
mode via local fallback): chat streams; Read, Edit and Bash round trips complete over four turns; input tokens now
reach Claude Code. Its `/cost` dollars are priced as Claude, so they mean nothing for a local model.

## Known gaps to look at

- Anthropic-only request features (prompt caching `cache_control`, extended thinking, server tools like web search)
  pass through untouched to Anthropic but are dropped when translated for an OpenAI-compatible model.
- `anthropic-beta` headers are not forwarded yet. If Claude Code depends on one, forward it in
  `GatewayEngine.SendAsync` for same-dialect targets.
