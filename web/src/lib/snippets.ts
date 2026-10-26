// Copy-paste setup for the tools people point at Fairlead. Pure functions of the gateway address and a key,
// so they are unit-tested and the Clients page and the setup wizard show exactly the same text.

export type Snippet = { id: string; tool: string; kind: 'shell' | 'python' | 'js' | 'json' | 'env'; note: string; code: string; status: 'ready' | 'hackathon' };

export function snippets(base: string, key: string): Snippet[] {
  const root = base.replace(/\/v1\/?$/, '');
  const k = key || 'fl-YOUR-KEY';
  return [
    {
      id: 'claude-code',
      tool: 'Claude Code',
      kind: 'shell',
      status: 'hackathon',
      note: 'Claude Code speaks the Anthropic API; Fairlead answers it from any route, local models included. Model names like claude-sonnet-* land on the smart route through the aliases.',
      code: `$env:ANTHROPIC_BASE_URL = "${root}"\n$env:ANTHROPIC_AUTH_TOKEN = "${k}"\nclaude`,
    },
    {
      id: 'openai-python',
      tool: 'OpenAI SDK (Python)',
      kind: 'python',
      status: 'ready',
      note: 'Any OpenAI-compatible client works the same way: base URL plus key. Use a route name as the model.',
      code: `from openai import OpenAI\n\nclient = OpenAI(base_url="${base}", api_key="${k}")\nreply = client.chat.completions.create(\n    model="auto",  # or smart, fast, local, ollama/qwen2.5:14b\n    messages=[{"role": "user", "content": "Hello from the hackathon"}],\n)\nprint(reply.choices[0].message.content)`,
    },
    {
      id: 'openai-node',
      tool: 'OpenAI SDK (Node / TypeScript)',
      kind: 'js',
      status: 'ready',
      note: 'Streaming works too: pass stream: true and iterate.',
      code: `import OpenAI from "openai";\n\nconst client = new OpenAI({ baseURL: "${base}", apiKey: "${k}" });\nconst stream = await client.chat.completions.create({\n  model: "auto",\n  messages: [{ role: "user", content: "Hello" }],\n  stream: true,\n});\nfor await (const part of stream) process.stdout.write(part.choices[0]?.delta?.content ?? "");`,
    },
    {
      id: 'anthropic-python',
      tool: 'Anthropic SDK (Python)',
      kind: 'python',
      status: 'ready',
      note: 'The Anthropic Messages API, served from any route.',
      code: `import anthropic\n\nclient = anthropic.Anthropic(base_url="${root}", api_key="${k}")\nmsg = client.messages.create(model="smart", max_tokens=512, messages=[{"role": "user", "content": "Hello"}])\nprint(msg.content[0].text)`,
    },
    {
      id: 'curl',
      tool: 'curl / any HTTP client',
      kind: 'shell',
      status: 'ready',
      note: 'X-Fairlead-Route picks a route whatever the model says; X-Fairlead-Privacy: local keeps a request on this machine.',
      code: `curl ${base}/chat/completions \\\n  -H "Authorization: Bearer ${k}" \\\n  -H "Content-Type: application/json" \\\n  -H "X-Fairlead-Privacy: local" \\\n  -d '{"model":"auto","messages":[{"role":"user","content":"hi"}]}'`,
    },
    {
      id: 'env',
      tool: 'Generic .env',
      kind: 'env',
      status: 'ready',
      note: 'Most frameworks (LangChain, LlamaIndex, Vercel AI SDK, LiteLLM clients) read these.',
      code: `OPENAI_BASE_URL=${base}\nOPENAI_API_BASE=${base}\nOPENAI_API_KEY=${k}\nANTHROPIC_BASE_URL=${root}\nANTHROPIC_API_KEY=${k}`,
    },
    {
      id: 'open-webui',
      tool: 'Open WebUI',
      kind: 'env',
      status: 'hackathon',
      note: 'Admin Settings > Connections > OpenAI API. Fairlead listens on this machine only, so run Open WebUI on the same PC, not in a container.',
      code: `OPENAI_API_BASE_URL=${base}\nOPENAI_API_KEY=${k}`,
    },
    {
      id: 'continue',
      tool: 'Continue (VS Code)',
      kind: 'json',
      status: 'hackathon',
      note: 'config.yaml / config.json model entry. Pick routes as models.',
      code: `{\n  "models": [\n    { "title": "Fairlead auto", "provider": "openai", "model": "auto", "apiBase": "${base}", "apiKey": "${k}" },\n    { "title": "Fairlead local", "provider": "openai", "model": "local", "apiBase": "${base}", "apiKey": "${k}" }\n  ]\n}`,
    },
    {
      id: 'aider',
      tool: 'Aider',
      kind: 'shell',
      status: 'hackathon',
      note: 'Aider uses LiteLLM model names: prefix the route with openai/.',
      code: `$env:OPENAI_API_BASE = "${base}"\n$env:OPENAI_API_KEY = "${k}"\naider --model openai/smart`,
    },
    {
      id: 'halyard',
      tool: 'Halyard (Fleet)',
      kind: 'env',
      status: 'hackathon',
      note: "Halyard's OpenAI profile posts to a full chat-completions URL. Add to config\\halyard.env.",
      code: `HALYARD_LLM_PROFILE=openai\nLLM_URL=${base}/chat/completions\nLLM_TOKEN=${k}\nLLM_MODEL=smart`,
    },
  ];
}

export function portOf(base: string): string {
  const m = /:(\d+)(?:\/|$)/.exec(base.replace(/^https?:\/\//, ''));
  return m ? m[1]! : '8776';
}
