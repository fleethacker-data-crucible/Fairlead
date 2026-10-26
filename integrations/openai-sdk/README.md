# OpenAI SDKs (Python, Node), curl, and anything OpenAI-compatible

**Status:** Verified (chat, streaming, tools; through Ollama and in the gateway tests).

```python
from openai import OpenAI
client = OpenAI(base_url="http://127.0.0.1:8776/v1", api_key="fl-YOUR-KEY")
r = client.chat.completions.create(model="auto", messages=[{"role": "user", "content": "hi"}])
```

```js
import OpenAI from "openai";
const client = new OpenAI({ baseURL: "http://127.0.0.1:8776/v1", apiKey: "fl-YOUR-KEY" });
```

```bash
curl http://127.0.0.1:8776/v1/chat/completions -H "Authorization: Bearer fl-YOUR-KEY" \
  -H "Content-Type: application/json" -d '{"model":"fast","messages":[{"role":"user","content":"hi"}]}'
```

Also served: `GET /v1/models` (routes first, then every `provider/model`), `POST /v1/embeddings` (OpenAI-compatible
providers; use `provider/model` for an embedding model, e.g. `ollama/nomic-embed-text`).

Not served yet: the Responses API (`/v1/responses`), Assistants, files, images, audio. See the roadmap.
