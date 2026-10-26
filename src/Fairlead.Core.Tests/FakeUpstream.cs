using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fairlead.Core.Tests
{
    /// <summary>A scripted model provider: answers by host, records every request it saw.</summary>
    public sealed class FakeUpstream : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, JObject, Task<HttpResponseMessage>>> _hosts = new ConcurrentDictionary<string, Func<HttpRequestMessage, JObject, Task<HttpResponseMessage>>>(StringComparer.OrdinalIgnoreCase);

        public List<KeyValuePair<string, JObject>> Seen { get; } = new List<KeyValuePair<string, JObject>>();

        public FakeUpstream On(string host, Func<HttpRequestMessage, JObject, Task<HttpResponseMessage>> handler)
        {
            _hosts[host] = handler;
            return this;
        }

        public FakeUpstream On(string host, Func<HttpRequestMessage, JObject, HttpResponseMessage> handler)
        {
            return On(host, (r, b) => Task.FromResult(handler(r, b)));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            var body = text.StartsWith("{") ? JObject.Parse(text) : new JObject();
            lock (Seen) Seen.Add(new KeyValuePair<string, JObject>(request.RequestUri!.Host + request.RequestUri.AbsolutePath, body));
            Func<HttpRequestMessage, JObject, Task<HttpResponseMessage>>? handler;
            if (!_hosts.TryGetValue(request.RequestUri!.Host, out handler)) throw new HttpRequestException("No such host is known (" + request.RequestUri.Host + ")");
            return await handler(request, body).ConfigureAwait(false);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, JToken body)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        }

        public static HttpResponseMessage Sse(params string[] events)
        {
            var sb = new StringBuilder();
            foreach (var e in events) sb.Append(e);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
        }

        public static JObject OpenAiAnswer(string text, string model = "m")
        {
            return new JObject
            {
                ["id"] = "chatcmpl-1",
                ["object"] = "chat.completion",
                ["model"] = model,
                ["choices"] = new JArray { new JObject { ["index"] = 0, ["message"] = new JObject { ["role"] = "assistant", ["content"] = text }, ["finish_reason"] = "stop" } },
                ["usage"] = new JObject { ["prompt_tokens"] = 11, ["completion_tokens"] = 3, ["total_tokens"] = 14 },
            };
        }

        public static JObject AnthropicAnswer(string text)
        {
            return new JObject
            {
                ["id"] = "msg_1",
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                ["stop_reason"] = "end_turn",
                ["usage"] = new JObject { ["input_tokens"] = 9, ["output_tokens"] = 2 },
            };
        }

        public static string OpenAiChunk(string text)
        {
            return "data: " + new JObject { ["id"] = "c", ["object"] = "chat.completion.chunk", ["choices"] = new JArray { new JObject { ["index"] = 0, ["delta"] = new JObject { ["content"] = text }, ["finish_reason"] = null } } }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
        }

        public static string OpenAiFinish(string reason = "stop")
        {
            return "data: " + new JObject { ["id"] = "c", ["object"] = "chat.completion.chunk", ["choices"] = new JArray { new JObject { ["index"] = 0, ["delta"] = new JObject(), ["finish_reason"] = reason } } }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
        }

        public static string OpenAiUsage(int input, int output)
        {
            return "data: " + new JObject { ["id"] = "c", ["object"] = "chat.completion.chunk", ["choices"] = new JArray(), ["usage"] = new JObject { ["prompt_tokens"] = input, ["completion_tokens"] = output } }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
        }
    }
}
