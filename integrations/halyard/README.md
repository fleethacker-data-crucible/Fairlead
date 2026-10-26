# Halyard (Fleet)

**Status:** Hackathon (not verified yet)

Halyard's OpenAI profile posts to a full chat-completions URL. In `config\halyard.env`:

```
HALYARD_LLM_PROFILE=openai
LLM_URL=http://127.0.0.1:8776/v1/chat/completions
LLM_TOKEN=fl-YOUR-KEY
LLM_MODEL=smart
```

Halyard then gets failover, Kedge mode and private-request routing without code changes. Backlog text can contain
names and internal details: consider `LLM_MODEL=local`, or a Fairlead rule on the `halyard` client name that sends it
to `local`, for sensitive backlogs.

## Checklist

- [ ] Sizing, refinement and vision drafts work through `smart`
- [ ] Halyard's transcripts still record the request and response
- [ ] Kedge mode: the sizing run completes on a local model
- [ ] Decide whether Halyard's own LLM settings page should offer "Fairlead" as a profile (a Halyard change)
