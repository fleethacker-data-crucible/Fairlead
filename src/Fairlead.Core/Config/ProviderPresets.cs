using System;
using System.Collections.Generic;
using System.Linq;

namespace Fairlead.Core.Config
{
    public sealed class ProviderPreset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ProviderKind Kind { get; set; }
        public string BaseUrl { get; set; }
        public bool Local { get; set; }
        public string ApiKeyEnv { get; set; }
        public bool NeedsKey { get; set; }
        /// <summary>Where to get a key, shown in the setup wizard.</summary>
        public string KeyUrl { get; set; }
        public string Hint { get; set; }

        public ProviderPreset()
        {
            Id = string.Empty;
            Name = string.Empty;
            BaseUrl = string.Empty;
            Hint = string.Empty;
        }
    }

    /// <summary>
    /// Known endpoints. Model ids are not hard-coded: Fairlead lists them from each provider's /models endpoint,
    /// so the catalogue stays current without a release.
    /// </summary>
    public static class ProviderPresets
    {
        public static readonly IReadOnlyList<ProviderPreset> All = new List<ProviderPreset>
        {
            new ProviderPreset { Id = "ollama", Name = "Ollama", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:11434", Local = true, Hint = "Local models pulled with `ollama pull`." },
            new ProviderPreset { Id = "phoenix-lai", Name = "phoenix-lai", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:1234/v1", Local = true, Hint = "The Fleet's local chat model (Gemma). Same address as LM Studio's default server, so run one of them; Fleet tools default to this address." },
            new ProviderPreset { Id = "lmstudio", Name = "LM Studio", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:1234/v1", Local = true, Hint = "Start the local server in LM Studio (Developer tab)." },
            new ProviderPreset { Id = "llamacpp", Name = "llama.cpp server", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:8080/v1", Local = true, Hint = "llama-server -m model.gguf --port 8080" },
            new ProviderPreset { Id = "vllm", Name = "vLLM", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:8000/v1", Local = true, Hint = "vllm serve <model> (a GPU box on the network also counts as local)." },
            new ProviderPreset { Id = "jan", Name = "Jan", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:1337/v1", Local = true, Hint = "Enable the local API server in Jan." },
            new ProviderPreset { Id = "anthropic", Name = "Anthropic", Kind = ProviderKind.Anthropic, BaseUrl = "https://api.anthropic.com", ApiKeyEnv = "ANTHROPIC_API_KEY", NeedsKey = true, KeyUrl = "https://console.anthropic.com/settings/keys", Hint = "Claude models." },
            new ProviderPreset { Id = "openai", Name = "OpenAI", Kind = ProviderKind.OpenAi, BaseUrl = "https://api.openai.com/v1", ApiKeyEnv = "OPENAI_API_KEY", NeedsKey = true, KeyUrl = "https://platform.openai.com/api-keys", Hint = "GPT models." },
            new ProviderPreset { Id = "gemini", Name = "Google Gemini", Kind = ProviderKind.OpenAi, BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", ApiKeyEnv = "GEMINI_API_KEY", NeedsKey = true, KeyUrl = "https://aistudio.google.com/apikey", Hint = "Gemini through its OpenAI-compatible endpoint." },
            new ProviderPreset { Id = "openrouter", Name = "OpenRouter", Kind = ProviderKind.OpenAi, BaseUrl = "https://openrouter.ai/api/v1", ApiKeyEnv = "OPENROUTER_API_KEY", NeedsKey = true, KeyUrl = "https://openrouter.ai/keys", Hint = "Hundreds of models behind one key." },
            new ProviderPreset { Id = "groq", Name = "Groq", Kind = ProviderKind.OpenAi, BaseUrl = "https://api.groq.com/openai/v1", ApiKeyEnv = "GROQ_API_KEY", NeedsKey = true, KeyUrl = "https://console.groq.com/keys", Hint = "Very fast open models; a good fast route." },
            new ProviderPreset { Id = "mistral", Name = "Mistral", Kind = ProviderKind.OpenAi, BaseUrl = "https://api.mistral.ai/v1", ApiKeyEnv = "MISTRAL_API_KEY", NeedsKey = true, KeyUrl = "https://console.mistral.ai/api-keys", Hint = "Mistral models." },
            new ProviderPreset { Id = "xai", Name = "xAI", Kind = ProviderKind.OpenAi, BaseUrl = "https://api.x.ai/v1", ApiKeyEnv = "XAI_API_KEY", NeedsKey = true, KeyUrl = "https://console.x.ai", Hint = "Grok models." },
            new ProviderPreset { Id = "together", Name = "Together AI", Kind = ProviderKind.OpenAi, BaseUrl = "https://api.together.xyz/v1", ApiKeyEnv = "TOGETHER_API_KEY", NeedsKey = true, KeyUrl = "https://api.together.ai/settings/api-keys", Hint = "Open models in the cloud." },
            new ProviderPreset { Id = "custom", Name = "Custom (OpenAI-compatible)", Kind = ProviderKind.OpenAi, BaseUrl = "http://127.0.0.1:8000/v1", Hint = "Any server that speaks /v1/chat/completions." },
        };

        /// <summary>
        /// The local presets grouped by address: presets that default to the same address (phoenix-lai and LM Studio both
        /// use port 1234) are one server to look for, so local detection probes and reports each address once. The first
        /// preset of a group is the one setup offers; the order of <see cref="All"/> decides.
        /// </summary>
        public static List<List<ProviderPreset>> LocalByAddress()
        {
            var groups = new List<List<ProviderPreset>>();
            foreach (var p in All.Where(x => x.Local))
            {
                var key = p.BaseUrl.TrimEnd('/');
                var group = groups.FirstOrDefault(g => string.Equals(g[0].BaseUrl.TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase));
                if (group == null) groups.Add(new List<ProviderPreset> { p });
                else group.Add(p);
            }
            return groups;
        }

        public static ProviderPreset Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static ProviderConfig Create(string presetId, string id = null)
        {
            var p = Find(presetId);
            if (p == null) throw new ArgumentException("Unknown provider preset '" + presetId + "'.");
            return new ProviderConfig
            {
                Id = string.IsNullOrEmpty(id) ? p.Id : id,
                Name = p.Name,
                Kind = p.Kind,
                Preset = p.Id,
                BaseUrl = p.BaseUrl,
                Local = p.Local,
                ApiKeyEnv = p.ApiKeyEnv,
                TimeoutSeconds = p.Local ? 600 : 120,
            };
        }
    }
}
