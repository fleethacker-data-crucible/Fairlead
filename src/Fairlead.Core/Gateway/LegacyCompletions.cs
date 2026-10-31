using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Host;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Gateway
{
    /// <summary>
    /// The legacy <c>POST /v1/completions</c> that some autocomplete plugins still call: the prompt becomes one user message of
    /// a chat request, which goes through the gateway like any other (keys, routes, privacy, failover, request log), and the
    /// answer comes back as a <c>text_completion</c>. Streaming is translated chunk by chunk.
    /// </summary>
    public static class LegacyCompletions
    {
        public static async Task<HttpResponse> HandleAsync(GatewayEngine engine, HttpRequest request)
        {
            JObject body;
            try { body = JObject.Parse(request.BodyText); }
            catch (JsonException ex) { return Fail(400, "Body is not valid JSON: " + ex.Message); }
            string error;
            var chat = ToChat(body, out error);
            if (chat == null) return Fail(400, error);

            var inner = new HttpRequest
            {
                Method = request.Method,
                Path = request.Path, // the request log keeps the path the caller used
                Query = request.Query,
                Body = Encoding.UTF8.GetBytes(chat.ToString(Formatting.None)),
                RemoteEndPoint = request.RemoteEndPoint,
                RequestId = request.RequestId,
                Aborted = request.Aborted,
                Internal = request.Internal,
            };
            foreach (var h in request.Headers) inner.Headers[h.Key] = h.Value;
            inner.Headers["Content-Type"] = "application/json";

            var response = await engine.HandleChatAsync(inner, Dialect.OpenAi).ConfigureAwait(false);
            if (response.Status != 200) return response; // errors are already in the OpenAI shape
            if (response.Stream != null)
            {
                var chatWriter = response.Stream;
                response.Stream = (output, token) => chatWriter(new CompletionStream(output), token);
                return response;
            }
            try { response.Body = Encoding.UTF8.GetBytes(FromChat(JObject.Parse(Encoding.UTF8.GetString(response.Body))).ToString(Formatting.None)); }
            catch (JsonException) { }
            return response;
        }

        private static HttpResponse Fail(int status, string message)
        {
            return HttpResponse.Json(status, Translate.Error(Dialect.OpenAi, status, message).ToString(Formatting.None));
        }

        /// <summary>The chat request for a completions request, or null with the reason it cannot be one.</summary>
        public static JObject ToChat(JObject body, out string error)
        {
            error = null;
            var prompt = body["prompt"];
            string text = null;
            if (prompt != null && prompt.Type == JTokenType.String) text = (string)prompt;
            else if (prompt is JArray && ((JArray)prompt).Count == 1 && ((JArray)prompt)[0].Type == JTokenType.String) text = (string)((JArray)prompt)[0];
            else if (prompt is JArray && ((JArray)prompt).Count > 1) { error = "Send one prompt per request: batches of prompts are not supported."; return null; }
            if (string.IsNullOrEmpty(text)) { error = "'prompt' (a string) is required."; return null; }
            var n = body["n"];
            if (n != null && n.Type != JTokenType.Null && (int)n != 1) { error = "Only n = 1 is supported."; return null; }
            var echo = body["echo"];
            if (echo != null && echo.Type == JTokenType.Boolean && (bool)echo) { error = "'echo' is not supported."; return null; }
            var suffix = body["suffix"];
            if (suffix != null && suffix.Type == JTokenType.String && ((string)suffix).Length > 0) { error = "'suffix' (fill in the middle) is not supported."; return null; }

            var chat = new JObject { { "model", (string)body["model"] ?? string.Empty }, { "messages", new JArray { new JObject { { "role", "user" }, { "content", text } } } } };
            foreach (var name in new[] { "max_tokens", "temperature", "top_p", "stop", "stream", "seed", "presence_penalty", "frequency_penalty", "user" })
            {
                var v = body[name];
                if (v != null && v.Type != JTokenType.Null) chat[name] = v.DeepClone();
            }
            return chat;
        }

        /// <summary>A chat completion as a text completion.</summary>
        public static JObject FromChat(JObject chat)
        {
            var choices = new JArray();
            var src = chat["choices"] as JArray;
            if (src != null)
            {
                foreach (var c in src)
                {
                    var message = c.At("message");
                    var content = message.At("content");
                    choices.Add(new JObject
                    {
                        { "text", content != null && content.Type == JTokenType.String ? (string)content : string.Empty },
                        { "index", (int?)c.At("index") ?? 0 },
                        { "logprobs", JValue.CreateNull() },
                        { "finish_reason", (string)c.At("finish_reason") },
                    });
                }
            }
            var o = new JObject
            {
                { "id", ((string)chat["id"] ?? "cmpl-fairlead").Replace("chatcmpl", "cmpl") },
                { "object", "text_completion" },
                { "created", (long?)chat["created"] ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "model", (string)chat["model"] ?? string.Empty },
                { "choices", choices },
            };
            if (chat["usage"] != null) o["usage"] = chat["usage"].DeepClone();
            return o;
        }

        /// <summary>Turns each chat chunk the gateway writes into a completion chunk; keep-alives, [DONE] and errors go through unchanged.</summary>
        private sealed class CompletionStream : IResponseStream
        {
            private readonly IResponseStream _out;

            public CompletionStream(IResponseStream output)
            {
                _out = output;
            }

            public Task WriteAsync(byte[] data, CancellationToken token)
            {
                return _out.WriteAsync(data, token);
            }

            public Task WriteAsync(string text, CancellationToken token)
            {
                if (!text.StartsWith("data: {", StringComparison.Ordinal)) return _out.WriteAsync(text, token);
                JObject chunk;
                try { chunk = JObject.Parse(text.Substring(6).Trim()); }
                catch (JsonException) { return _out.WriteAsync(text, token); }
                if (chunk["error"] != null) return _out.WriteAsync(text, token);
                var choices = new JArray();
                var src = chunk["choices"] as JArray;
                if (src != null)
                {
                    foreach (var c in src)
                    {
                        var content = c.At("delta").At("content");
                        choices.Add(new JObject
                        {
                            { "text", content != null && content.Type == JTokenType.String ? (string)content : string.Empty },
                            { "index", (int?)c.At("index") ?? 0 },
                            { "logprobs", JValue.CreateNull() },
                            { "finish_reason", (string)c.At("finish_reason") },
                        });
                    }
                }
                var o = new JObject
                {
                    { "id", ((string)chunk["id"] ?? "cmpl-fairlead").Replace("chatcmpl", "cmpl") },
                    { "object", "text_completion" },
                    { "created", (long?)chunk["created"] ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                    { "model", (string)chunk["model"] ?? string.Empty },
                    { "choices", choices },
                };
                if (chunk["usage"] != null && chunk["usage"].Type == JTokenType.Object) o["usage"] = chunk["usage"].DeepClone();
                return _out.WriteAsync(SseWriter.Data(o), token);
            }
        }
    }
}
