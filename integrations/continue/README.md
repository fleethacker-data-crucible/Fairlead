# Continue (VS Code / JetBrains)

**Status:** Hackathon (not verified yet)

```json
{
  "models": [
    { "title": "Fairlead auto", "provider": "openai", "model": "auto", "apiBase": "http://127.0.0.1:8776/v1", "apiKey": "fl-YOUR-KEY" },
    { "title": "Fairlead local", "provider": "openai", "model": "local", "apiBase": "http://127.0.0.1:8776/v1", "apiKey": "fl-YOUR-KEY" }
  ],
  "tabAutocompleteModel": { "title": "Fairlead fast", "provider": "openai", "model": "fast", "apiBase": "http://127.0.0.1:8776/v1", "apiKey": "fl-YOUR-KEY" }
}
```

## Checklist

- [ ] Chat and edit work through `auto`
- [ ] Autocomplete on `fast` is quick enough (consider a hedge on the fast route)
- [ ] Does Continue need `/v1/completions` (legacy, non-chat) for autocomplete? If so, add it to Fairlead
- [ ] Kedge mode: still works offline
