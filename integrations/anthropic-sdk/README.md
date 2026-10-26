# Anthropic SDK and the Messages API

**Status:** Verified (streaming SSE and `tool_use`, translated to and from an Ollama model).

```python
import anthropic
client = anthropic.Anthropic(base_url="http://127.0.0.1:8776", api_key="fl-YOUR-KEY")
msg = client.messages.create(model="smart", max_tokens=512, messages=[{"role": "user", "content": "hi"}])
```

When the target is Anthropic, the request is relayed as sent (model swapped), so every feature works. When the
target speaks the OpenAI dialect, Fairlead translates: system, text, images, tools, tool results, stop sequences,
`tool_choice` and streaming events map across; thinking blocks, prompt caching and server tools are dropped.
`POST /v1/messages/count_tokens` returns an estimate.
