# Aider

**Status:** Hackathon (not verified yet)

```powershell
$env:OPENAI_API_BASE = "http://127.0.0.1:8776/v1"
$env:OPENAI_API_KEY = "fl-YOUR-KEY"
aider --model openai/smart
```

Aider goes through LiteLLM, which needs the `openai/` prefix to treat the name as an OpenAI-compatible model.

## Checklist

- [ ] Edits apply with `openai/smart`
- [ ] `--weak-model openai/fast` for commit messages and summaries
- [ ] Kedge mode: edits still apply with the local model (diff format quality varies by model)
- [ ] Silence LiteLLM's "unknown model" warnings with a model settings file if they get noisy
