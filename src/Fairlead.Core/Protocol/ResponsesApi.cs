using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Protocol
{
    /// <summary>A request the Responses endpoint cannot take as configured (images, tools); the gateway answers 400 for it.</summary>
    public sealed class ResponsesRequestException : Exception
    {
        public ResponsesRequestException(string message) : base(message) { }
    }

    /// <summary>
    /// OpenAI Chat Completions to and from the OpenAI Responses API (request format openai_responses_v1), for AI
    /// gateways that only serve /responses. Text only: tools, images and streaming are not assumed until they are
    /// tested against the gateway, so a request that needs them is refused here instead of being half-translated.
    /// </summary>
    public static class ResponsesApi
    {
        /// <summary>Optional request fields this translator knows how to fill.</summary>
        public static readonly string[] KnownOptionalFields = { "max_output_tokens", "temperature", "top_p" };

        /// <summary>
        /// The Responses body for a Chat Completions body: model, input (one item per message) and only the optional
        /// fields the endpoint lists. Never stream, tools or anything from the gateway file's metadata.
        /// </summary>
        public static JObject FromChatRequest(JObject chat, string model, IList<string> optionalFields)
        {
            if (Translate.Arr(chat["tools"]).Any()) throw new ResponsesRequestException("This gateway model is not set up for tool calls.");
            var input = new JArray();
            foreach (var m in Translate.Arr(chat["messages"]))
            {
                var role = (string)m["role"] ?? "user";
                if (role == "tool" || m["tool_calls"] != null) throw new ResponsesRequestException("This gateway model is not set up for tool calls.");
                if (role == "function") throw new ResponsesRequestException("Function messages are not supported by this gateway model.");
                if (role != "system" && role != "developer" && role != "user" && role != "assistant") throw new ResponsesRequestException("Unknown message role '" + role + "'.");
                var partType = role == "assistant" ? "output_text" : "input_text";
                var content = new JArray();
                var c = m["content"];
                if (c != null && c.Type == JTokenType.String)
                {
                    content.Add(new JObject { { "type", partType }, { "text", (string)c } });
                }
                else
                {
                    foreach (var part in Translate.Arr(c))
                    {
                        var type = (string)part["type"];
                        if (type == "text") content.Add(new JObject { { "type", partType }, { "text", (string)part["text"] ?? string.Empty } });
                        else if (type == "image_url" || type == "input_image" || type == "image") throw new ResponsesRequestException("This gateway model is not set up for image input.");
                        else throw new ResponsesRequestException("Message content of type '" + type + "' is not supported by this gateway model.");
                    }
                }
                if (content.Count == 0) continue;
                input.Add(new JObject { { "role", role }, { "content", content } });
            }
            if (input.Count == 0) throw new ResponsesRequestException("The request has no text to send.");
            var body = new JObject { { "model", model }, { "input", input } };
            var allowed = optionalFields ?? new List<string>();
            if (allowed.Contains("max_output_tokens"))
            {
                var max = chat["max_completion_tokens"] ?? chat["max_tokens"];
                if (max != null && max.Type == JTokenType.Integer) body["max_output_tokens"] = max;
            }
            if (allowed.Contains("temperature")) CopyNumber(chat, body, "temperature");
            if (allowed.Contains("top_p")) CopyNumber(chat, body, "top_p");
            return body;
        }

        private static void CopyNumber(JObject from, JObject to, string name)
        {
            var v = from[name];
            if (v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer)) to[name] = v;
        }

        /// <summary>The answer text: every output_text part of every message in output, or the top-level output_text.</summary>
        public static string Text(JObject response)
        {
            var sb = new StringBuilder();
            var any = false;
            foreach (var item in Translate.Arr(response["output"]))
            {
                var type = (string)item["type"];
                if (type != null && type != "message") continue;
                foreach (var part in Translate.Arr(item["content"]))
                {
                    var pt = (string)part["type"];
                    if (pt == "output_text" || pt == "text")
                    {
                        sb.Append((string)part["text"]);
                        any = true;
                    }
                }
            }
            if (!any)
            {
                var top = response["output_text"];
                if (top != null && top.Type == JTokenType.String) return (string)top;
                return null;
            }
            return sb.ToString();
        }

        /// <summary>A Chat Completions answer for a Responses answer; null when the answer holds no text.</summary>
        public static JObject ToChatCompletion(JObject response, string model)
        {
            var text = Text(response);
            if (text == null) return null;
            var status = (string)response["status"];
            var reason = (string)response["incomplete_details"].At("reason");
            var finish = status == "incomplete" && reason == "max_output_tokens" ? "length" : status == "incomplete" && reason == "content_filter" ? "content_filter" : "stop";
            var usage = response["usage"] as JObject;
            var o = new JObject
            {
                { "id", "chatcmpl-" + ((string)response["id"] ?? Guid.NewGuid().ToString("N").Substring(0, 24)) },
                { "object", "chat.completion" },
                { "created", (long?)response["created_at"] ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "model", model },
                { "choices", new JArray { new JObject { { "index", 0 }, { "message", new JObject { { "role", "assistant" }, { "content", text } } }, { "finish_reason", finish } } } },
            };
            if (usage != null)
            {
                var input = (int?)usage["input_tokens"];
                var output = (int?)usage["output_tokens"];
                o["usage"] = new JObject { { "prompt_tokens", input ?? 0 }, { "completion_tokens", output ?? 0 }, { "total_tokens", (int?)usage["total_tokens"] ?? (input ?? 0) + (output ?? 0) } };
            }
            return o;
        }

        /// <summary>
        /// A Chat Completions event stream carrying one complete answer: role and text, the finish reason, usage when
        /// asked for, then [DONE]. Used when a client streams but the gateway is called without streaming.
        /// </summary>
        public static string ToChatStream(JObject completion, bool includeUsage)
        {
            var id = (string)completion["id"];
            var created = completion["created"];
            var model = completion["model"];
            var choice = completion["choices"].At(0);
            var sb = new StringBuilder();
            Action<JObject> emit = chunk => sb.Append("data: ").Append(chunk.ToString(Formatting.None)).Append("\n\n");
            Func<JToken, JToken, JObject> chunkOf = (delta, finish) => new JObject
            {
                { "id", id },
                { "object", "chat.completion.chunk" },
                { "created", created },
                { "model", model },
                { "choices", new JArray { new JObject { { "index", 0 }, { "delta", delta }, { "finish_reason", finish } } } },
            };
            emit(chunkOf(new JObject { { "role", "assistant" }, { "content", (string)choice.At("message").At("content") ?? string.Empty } }, JValue.CreateNull()));
            emit(chunkOf(new JObject(), choice.At("finish_reason") ?? "stop"));
            if (includeUsage && completion["usage"] != null)
            {
                emit(new JObject { { "id", id }, { "object", "chat.completion.chunk" }, { "created", created }, { "model", model }, { "choices", new JArray() }, { "usage", completion["usage"] } });
            }
            sb.Append("data: [DONE]\n\n");
            return sb.ToString();
        }
    }
}
