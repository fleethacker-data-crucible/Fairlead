# LangChain, LlamaIndex, Vercel AI SDK and other frameworks

**Status:** Hackathon (not verified yet)

Most frameworks wrap the OpenAI or Anthropic SDK, so the SDK recipes apply:

```python
from langchain_openai import ChatOpenAI
llm = ChatOpenAI(base_url="http://127.0.0.1:8776/v1", api_key="fl-YOUR-KEY", model="auto")

from llama_index.llms.openai_like import OpenAILike
llm = OpenAILike(api_base="http://127.0.0.1:8776/v1", api_key="fl-YOUR-KEY", model="auto", is_chat_model=True)
```

```ts
import { createOpenAI } from "@ai-sdk/openai";
const fairlead = createOpenAI({ baseURL: "http://127.0.0.1:8776/v1", apiKey: "fl-YOUR-KEY" });
```

## Checklist

- [ ] Chat, streaming, tool calling in each framework the teams pick
- [ ] Embeddings through `POST /v1/embeddings` with an embedding model (`ollama/nomic-embed-text`)
- [ ] Structured output (`response_format: json_schema`): relayed to OpenAI-compatible targets; not translated for Anthropic targets yet
