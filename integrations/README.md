# Integrations

Every tool connects the same way: **base URL + client key + a route as the model**. There is no per-tool code in
Fairlead; an integration is a verified recipe, plus fixes in Fairlead when a tool trips over something.

| | |
|---|---|
| OpenAI-style base URL | `http://127.0.0.1:8776/v1` |
| Anthropic-style base URL | `http://127.0.0.1:8776` (the SDK adds `/v1/messages`) |
| Key | a client key from Fairlead → Clients (`fl-…`), as `Authorization: Bearer` or `x-api-key` |
| Model | `auto`, `smart`, `fast`, `local`, any route you made, or `provider/model` |
| From a container | `http://host.docker.internal:8776/v1` with `FAIRLEAD_BIND=lan` |

The Clients page renders these recipes with your real address and a freshly created key.

## Status

**Verified** means checked against a running Fairlead with a real model. **Hackathon** means the recipe is written
but someone has to run it with the real tool and tick the checklist in its folder.

| Tool | Folder | Status | Verified so far |
|---|---|---|---|
| Any OpenAI SDK / curl | [openai-sdk](openai-sdk/README.md) | Verified | chat, streaming, tools, through Ollama and the gateway tests |
| Anthropic SDK / Messages API | [anthropic-sdk](anthropic-sdk/README.md) | Verified | streaming SSE and `tool_use` translated to and from Ollama |
| Claude Code | [claude-code](claude-code/README.md) | Hackathon | its wire format (Messages + streaming + tools + count_tokens) works; run the CLI end to end |
| Open WebUI | [open-webui](open-webui/README.md) | Hackathon | |
| Continue (VS Code) | [continue](continue/README.md) | Hackathon | |
| Aider | [aider](aider/README.md) | Hackathon | |
| LangChain / LlamaIndex | [langchain](langchain/README.md) | Hackathon | |
| Halyard (Fleet) | [halyard](halyard/README.md) | Hackathon | |
| Windlass (huge local models) | [windlass](windlass/README.md) | Hackathon | contract defined; Windlass is being built in its own repo |

To add a tool, copy [`_template`](_template/README.md).
