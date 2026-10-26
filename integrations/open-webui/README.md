# Open WebUI

**Status:** Hackathon (not verified yet)

## Setup

Fairlead must listen on the LAN for the container to reach it: add `FAIRLEAD_BIND=lan` to `config\fairlead.env`
and restart Fairlead. Then in Open WebUI: Admin Settings > Connections > OpenAI API.

```
API Base URL: http://host.docker.internal:8776/v1
API Key:      fl-YOUR-KEY   (create one named open-webui)
```

Or as container environment: `OPENAI_API_BASE_URL=http://host.docker.internal:8776/v1`, `OPENAI_API_KEY=fl-...`.
The model picker shows Fairlead's routes first (`auto`, `smart`, `fast`, `local`), then every provider/model.

## Checklist

- [ ] The model list loads (it calls `GET /v1/models`)
- [ ] Chat and streaming work through `auto`
- [ ] Kedge mode forced on: chats keep working on the local model
- [ ] Title generation and follow-up suggestions (extra calls Open WebUI makes) land on `fast`
- [ ] Decide: keep Open WebUI's own direct Ollama connection, or send everything through Fairlead
