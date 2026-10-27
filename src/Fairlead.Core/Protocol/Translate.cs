using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Protocol
{
    /// <summary>The two wire formats the gateway speaks on both sides.</summary>
    public enum Dialect { OpenAi, Anthropic }

    public sealed class TokenUsage
    {
        public int? Input { get; set; }
        public int? Output { get; set; }
        public int? CacheRead { get; set; }

        public bool Known { get { return Input.HasValue || Output.HasValue; } }

        public static TokenUsage FromOpenAi(JToken usage)
        {
            var u = new TokenUsage();
            if (usage == null || usage.Type != JTokenType.Object) return u;
            u.Input = (int?)usage["prompt_tokens"];
            u.Output = (int?)usage["completion_tokens"];
            u.CacheRead = (int?)usage["prompt_tokens_details"].At("cached_tokens");
            return u;
        }

        public static TokenUsage FromAnthropic(JToken usage)
        {
            var u = new TokenUsage();
            if (usage == null || usage.Type != JTokenType.Object) return u;
            var input = (int?)usage["input_tokens"];
            var cacheRead = (int?)usage["cache_read_input_tokens"];
            var cacheWrite = (int?)usage["cache_creation_input_tokens"];
            if (input.HasValue || cacheRead.HasValue || cacheWrite.HasValue) u.Input = (input ?? 0) + (cacheRead ?? 0) + (cacheWrite ?? 0);
            u.Output = (int?)usage["output_tokens"];
            u.CacheRead = cacheRead;
            return u;
        }

        public void Merge(TokenUsage other)
        {
            if (other.Input.HasValue) Input = other.Input;
            if (other.Output.HasValue) Output = other.Output;
            if (other.CacheRead.HasValue) CacheRead = other.CacheRead;
        }
    }

    /// <summary>
    /// OpenAI Chat Completions and Anthropic Messages, translated both ways: requests, complete responses, and
    /// (in <see cref="OpenAiToAnthropicStream"/> / <see cref="AnthropicToOpenAiStream"/>) streams. Same-dialect traffic is
    /// never translated; it is relayed with only the model swapped, so provider-specific features survive.
    /// </summary>
    public static class Translate
    {
        // ---------- requests ----------

        public static JObject AnthropicToOpenAiRequest(JObject a, string model, bool maxCompletionTokens, bool streamUsage)
        {
            var o = new JObject { { "model", model } };
            var messages = new JArray();
            var system = SystemText(a["system"]);
            if (!string.IsNullOrEmpty(system)) messages.Add(new JObject { { "role", "system" }, { "content", system } });
            // System messages inside the conversation (Claude Code sends them) become user turns: many local chat
            // templates reject a system message anywhere but first. One that lands between tool calls and their
            // results waits until after the results, since role=tool messages must directly follow the calls.
            var deferred = new List<JObject>();
            foreach (var m in Arr(a["messages"]))
            {
                var role = (string)m["role"] ?? "user";
                var content = m["content"];
                if (role == "system")
                {
                    var noteText = content == null ? null : content.Type == JTokenType.String ? (string)content : BlocksText(content);
                    if (string.IsNullOrEmpty(noteText)) continue;
                    var note = new JObject { { "role", "user" }, { "content", noteText } };
                    var last = messages.Count > 0 ? messages[messages.Count - 1] : null;
                    if (last != null && last["tool_calls"] != null) deferred.Add(note);
                    else messages.Add(note);
                    continue;
                }
                if (role != "assistant" && content != null && content.Type != JTokenType.String) AddToolResults(messages, content);
                foreach (var note in deferred) messages.Add(note);
                deferred.Clear();
                if (content == null || content.Type == JTokenType.String)
                {
                    messages.Add(new JObject { { "role", role }, { "content", content == null ? string.Empty : (string)content } });
                    continue;
                }
                if (role == "assistant")
                {
                    var text = new StringBuilder();
                    var calls = new JArray();
                    foreach (var b in Arr(content))
                    {
                        var type = (string)b["type"];
                        if (type == "text") text.Append((string)b["text"]);
                        else if (type == "tool_use")
                        {
                            calls.Add(new JObject
                            {
                                { "id", (string)b["id"] },
                                { "type", "function" },
                                { "function", new JObject { { "name", (string)b["name"] }, { "arguments", (b["input"] ?? new JObject()).ToString(Formatting.None) } } },
                            });
                        }
                        // thinking / redacted_thinking blocks are Anthropic-only and are dropped.
                    }
                    var msg = new JObject { { "role", "assistant" }, { "content", text.Length == 0 && calls.Count > 0 ? null : text.ToString() } };
                    if (calls.Count > 0) msg["tool_calls"] = calls;
                    messages.Add(msg);
                    continue;
                }
                // user: tool results were added above by AddToolResults; the rest becomes one user message.
                var parts = new JArray();
                foreach (var b in Arr(content))
                {
                    var type = (string)b["type"];
                    if (type == "text") parts.Add(new JObject { { "type", "text" }, { "text", (string)b["text"] } });
                    else if (type == "image")
                    {
                        var src = b["source"];
                        var url = (string)src.At("type") == "base64"
                            ? "data:" + (string)src.At("media_type") + ";base64," + (string)src.At("data")
                            : (string)src.At("url");
                        if (url != null) parts.Add(new JObject { { "type", "image_url" }, { "image_url", new JObject { { "url", url } } } });
                    }
                    else if (type == "document")
                    {
                        var src = b["source"];
                        if ((string)src.At("type") == "text") parts.Add(new JObject { { "type", "text" }, { "text", (string)src.At("data") } });
                    }
                }
                if (parts.Count == 1 && (string)parts[0]["type"] == "text") messages.Add(new JObject { { "role", "user" }, { "content", parts[0]["text"] } });
                else if (parts.Count > 0) messages.Add(new JObject { { "role", "user" }, { "content", parts } });
            }
            foreach (var note in deferred) messages.Add(note);
            o["messages"] = messages;
            var max = a["max_tokens"];
            if (max != null) o[maxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = max;
            Copy(a, o, "temperature");
            Copy(a, o, "top_p");
            var stops = a["stop_sequences"] as JArray;
            if (stops != null && stops.Count > 0) o["stop"] = stops;
            var stream = a.Value<bool?>("stream") == true;
            if (stream)
            {
                o["stream"] = true;
                if (streamUsage) o["stream_options"] = new JObject { { "include_usage", true } };
            }
            var tools = new JArray();
            foreach (var t in Arr(a["tools"]))
            {
                // Server tools (web_search, bash, computer...) carry a "type" and no schema; they have no OpenAI equivalent.
                if (t["input_schema"] == null) continue;
                tools.Add(new JObject
                {
                    { "type", "function" },
                    { "function", new JObject { { "name", (string)t["name"] }, { "description", (string)t["description"] ?? string.Empty }, { "parameters", t["input_schema"] } } },
                });
            }
            if (tools.Count > 0) o["tools"] = tools;
            var choice = a["tool_choice"];
            if (choice != null && tools.Count > 0)
            {
                switch ((string)choice["type"])
                {
                    case "any": o["tool_choice"] = "required"; break;
                    case "none": o["tool_choice"] = "none"; break;
                    case "tool": o["tool_choice"] = new JObject { { "type", "function" }, { "function", new JObject { { "name", (string)choice["name"] } } } }; break;
                    default: o["tool_choice"] = "auto"; break;
                }
                if (choice.Value<bool?>("disable_parallel_tool_use") == true) o["parallel_tool_calls"] = false;
            }
            var user = (string)a["metadata"].At("user_id");
            if (!string.IsNullOrEmpty(user)) o["user"] = user;
            return o;
        }

        public static JObject OpenAiToAnthropicRequest(JObject o, string model)
        {
            var a = new JObject { { "model", model } };
            var system = new List<string>();
            var messages = new JArray();
            foreach (var m in Arr(o["messages"]))
            {
                var role = (string)m["role"] ?? "user";
                if (role == "system" || role == "developer")
                {
                    var s = ContentText(m["content"]);
                    if (!string.IsNullOrEmpty(s)) system.Add(s);
                    continue;
                }
                if (role == "tool" || role == "function")
                {
                    var block = new JObject { { "type", "tool_result" }, { "tool_use_id", (string)m["tool_call_id"] ?? (string)m["name"] ?? "tool" }, { "content", ContentText(m["content"]) } };
                    AppendBlock(messages, "user", block);
                    continue;
                }
                if (role == "assistant")
                {
                    var text = ContentText(m["content"]);
                    if (!string.IsNullOrEmpty(text)) AppendBlock(messages, "assistant", new JObject { { "type", "text" }, { "text", text } });
                    foreach (var call in Arr(m["tool_calls"]))
                    {
                        AppendBlock(messages, "assistant", new JObject
                        {
                            { "type", "tool_use" },
                            { "id", (string)call["id"] ?? ("toolu_" + Guid.NewGuid().ToString("N").Substring(0, 20)) },
                            { "name", (string)call["function"].At("name") },
                            { "input", ParseArguments((string)call["function"].At("arguments")) },
                        });
                    }
                    continue;
                }
                var content = m["content"];
                if (content == null || content.Type == JTokenType.String)
                {
                    AppendBlock(messages, "user", new JObject { { "type", "text" }, { "text", (string)content ?? string.Empty } });
                    continue;
                }
                foreach (var part in Arr(content))
                {
                    var type = (string)part["type"];
                    if (type == "text") AppendBlock(messages, "user", new JObject { { "type", "text" }, { "text", (string)part["text"] } });
                    else if (type == "image_url")
                    {
                        var url = (string)part["image_url"].At("url") ?? string.Empty;
                        AppendBlock(messages, "user", ImageBlock(url));
                    }
                }
            }
            if (system.Count > 0) a["system"] = string.Join("\n\n", system);
            // Anthropic requires the conversation to start with a user turn.
            if (messages.Count > 0 && (string)messages[0]["role"] == "assistant")
                messages.Insert(0, new JObject { { "role", "user" }, { "content", new JArray { new JObject { { "type", "text" }, { "text", "(continue)" } } } } });
            a["messages"] = messages;
            var max = o["max_completion_tokens"] ?? o["max_tokens"];
            a["max_tokens"] = max != null && max.Type == JTokenType.Integer ? max : new JValue(4096);
            var temp = o["temperature"];
            if (temp != null && (temp.Type == JTokenType.Float || temp.Type == JTokenType.Integer)) a["temperature"] = Math.Min(1.0, Math.Max(0.0, (double)temp));
            Copy(o, a, "top_p");
            var stop = o["stop"];
            if (stop != null && stop.Type == JTokenType.String) a["stop_sequences"] = new JArray { stop };
            else if (stop is JArray && ((JArray)stop).Count > 0) a["stop_sequences"] = stop;
            if (o.Value<bool?>("stream") == true) a["stream"] = true;
            var tools = new JArray();
            foreach (var t in Arr(o["tools"]))
            {
                var f = t["function"];
                if (f == null) continue;
                tools.Add(new JObject
                {
                    { "name", (string)f["name"] },
                    { "description", (string)f["description"] ?? string.Empty },
                    { "input_schema", f["parameters"] ?? new JObject { { "type", "object" }, { "properties", new JObject() } } },
                });
            }
            if (tools.Count > 0) a["tools"] = tools;
            var choice = o["tool_choice"];
            if (choice != null && tools.Count > 0)
            {
                if (choice.Type == JTokenType.String)
                {
                    var c = (string)choice;
                    a["tool_choice"] = c == "required" ? new JObject { { "type", "any" } } : c == "none" ? new JObject { { "type", "none" } } : new JObject { { "type", "auto" } };
                }
                else if (choice["function"] != null) a["tool_choice"] = new JObject { { "type", "tool" }, { "name", (string)choice["function"].At("name") } };
                var tc = a["tool_choice"] as JObject;
                if (o.Value<bool?>("parallel_tool_calls") == false && tc != null) tc["disable_parallel_tool_use"] = true;
            }
            var user = (string)o["user"];
            if (!string.IsNullOrEmpty(user)) a["metadata"] = new JObject { { "user_id", user } };
            return a;
        }

        // ---------- complete responses ----------

        public static JObject OpenAiToAnthropicResponse(JObject o, string model)
        {
            var choice = Arr(o["choices"]).FirstOrDefault();
            var message = choice.At("message");
            var content = new JArray();
            var text = ContentText(message.At("content"));
            if (!string.IsNullOrEmpty(text)) content.Add(new JObject { { "type", "text" }, { "text", text } });
            foreach (var call in Arr(message.At("tool_calls")))
            {
                content.Add(new JObject
                {
                    { "type", "tool_use" },
                    { "id", (string)call["id"] ?? ("toolu_" + Guid.NewGuid().ToString("N").Substring(0, 20)) },
                    { "name", (string)call["function"].At("name") },
                    { "input", ParseArguments((string)call["function"].At("arguments")) },
                });
            }
            if (content.Count == 0) content.Add(new JObject { { "type", "text" }, { "text", string.Empty } });
            var usage = TokenUsage.FromOpenAi(o["usage"]);
            return new JObject
            {
                { "id", "msg_" + Guid.NewGuid().ToString("N").Substring(0, 24) },
                { "type", "message" },
                { "role", "assistant" },
                { "model", model },
                { "content", content },
                { "stop_reason", StopReasonToAnthropic((string)choice.At("finish_reason"), content.Any(b => (string)b["type"] == "tool_use")) },
                { "stop_sequence", null },
                { "usage", new JObject { { "input_tokens", usage.Input ?? 0 }, { "output_tokens", usage.Output ?? 0 } } },
            };
        }

        public static JObject AnthropicToOpenAiResponse(JObject a, string model)
        {
            var text = new StringBuilder();
            var calls = new JArray();
            foreach (var b in Arr(a["content"]))
            {
                var type = (string)b["type"];
                if (type == "text") text.Append((string)b["text"]);
                else if (type == "tool_use")
                {
                    calls.Add(new JObject
                    {
                        { "id", (string)b["id"] },
                        { "type", "function" },
                        { "function", new JObject { { "name", (string)b["name"] }, { "arguments", (b["input"] ?? new JObject()).ToString(Formatting.None) } } },
                    });
                }
            }
            var message = new JObject { { "role", "assistant" }, { "content", text.Length == 0 && calls.Count > 0 ? null : text.ToString() } };
            if (calls.Count > 0) message["tool_calls"] = calls;
            var usage = TokenUsage.FromAnthropic(a["usage"]);
            return new JObject
            {
                { "id", "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 24) },
                { "object", "chat.completion" },
                { "created", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                { "model", model },
                { "choices", new JArray
                {
                    new JObject { { "index", 0 }, { "message", message }, { "finish_reason", StopReasonToOpenAi((string)a["stop_reason"]) } },
                } },
                { "usage", new JObject { { "prompt_tokens", usage.Input ?? 0 }, { "completion_tokens", usage.Output ?? 0 }, { "total_tokens", (usage.Input ?? 0) + (usage.Output ?? 0) } } },
            };
        }

        /// <summary>An error body in the caller's dialect.</summary>
        public static JObject Error(Dialect dialect, int status, string message, string code = null)
        {
            if (dialect == Dialect.Anthropic)
            {
                return new JObject { { "type", "error" }, { "error", new JObject { { "type", AnthropicErrorType(status) }, { "message", message } } } };
            }
            return new JObject { { "error", new JObject { { "message", message }, { "type", OpenAiErrorType(status) }, { "code", code } } } };
        }

        // ---------- helpers ----------

        public static string StopReasonToAnthropic(string finish, bool hasToolUse)
        {
            switch (finish)
            {
                case "length": return "max_tokens";
                case "tool_calls":
                case "function_call": return "tool_use";
                case "stop": return hasToolUse ? "tool_use" : "end_turn";
                default: return hasToolUse ? "tool_use" : "end_turn";
            }
        }

        public static string StopReasonToOpenAi(string stop)
        {
            switch (stop)
            {
                case "max_tokens": return "length";
                case "tool_use": return "tool_calls";
                case "refusal": return "content_filter";
                default: return "stop";
            }
        }

        private static string AnthropicErrorType(int status)
        {
            switch (status)
            {
                case 400: return "invalid_request_error";
                case 401: return "authentication_error";
                case 403: return "permission_error";
                case 404: return "not_found_error";
                case 413: return "request_too_large";
                case 429: return "rate_limit_error";
                case 529: return "overloaded_error";
                default: return status >= 500 ? "api_error" : "invalid_request_error";
            }
        }

        private static string OpenAiErrorType(int status)
        {
            switch (status)
            {
                case 401: return "authentication_error";
                case 403: return "permission_error";
                case 404: return "not_found_error";
                case 429: return "rate_limit_error";
                default: return status >= 500 ? "server_error" : "invalid_request_error";
            }
        }

        private static JObject ImageBlock(string url)
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var semi = url.IndexOf(';');
                var comma = url.IndexOf(',');
                if (semi > 5 && comma > semi)
                {
                    return new JObject
                    {
                        { "type", "image" },
                        { "source", new JObject { { "type", "base64" }, { "media_type", url.Substring(5, semi - 5) }, { "data", url.Substring(comma + 1) } } },
                    };
                }
            }
            return new JObject { { "type", "image" }, { "source", new JObject { { "type", "url" }, { "url", url } } } };
        }

        /// <summary>Anthropic requires alternating roles; consecutive blocks of one role share a message.</summary>
        private static void AppendBlock(JArray messages, string role, JObject block)
        {
            var last = messages.Count > 0 ? messages[messages.Count - 1] as JObject : null;
            var blocks = last == null ? null : last["content"] as JArray;
            if (last != null && (string)last["role"] == role && blocks != null)
            {
                // Tool results must come before any text in the user turn that carries them.
                if ((string)block["type"] == "tool_result")
                {
                    var firstNonResult = 0;
                    while (firstNonResult < blocks.Count && (string)blocks[firstNonResult]["type"] == "tool_result") firstNonResult++;
                    blocks.Insert(firstNonResult, block);
                }
                else blocks.Add(block);
                return;
            }
            messages.Add(new JObject { { "role", role }, { "content", new JArray { block } } });
        }

        public static JObject ParseArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return new JObject();
            try
            {
                var t = JToken.Parse(arguments);
                return t as JObject ?? new JObject { { "value", t } };
            }
            catch (JsonException)
            {
                return new JObject { { "_raw", arguments } };
            }
        }

        /// <summary>Text of an OpenAI content value: a string or an array of parts.</summary>
        public static string ContentText(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return string.Empty;
            if (content.Type == JTokenType.String) return (string)content ?? string.Empty;
            var sb = new StringBuilder();
            foreach (var p in Arr(content))
            {
                var t = (string)p["text"];
                if (t != null) { if (sb.Length > 0) sb.Append('\n'); sb.Append(t); }
            }
            return sb.ToString();
        }

        /// <summary>Text of an Anthropic system / tool_result value: a string or an array of blocks.</summary>
        public static string BlocksText(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return string.Empty;
            if (value.Type == JTokenType.String) return (string)value ?? string.Empty;
            var sb = new StringBuilder();
            foreach (var b in Arr(value))
            {
                if ((string)b["type"] == "text")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append((string)b["text"]);
                }
            }
            return sb.ToString();
        }

        /// <summary>Anthropic tool_result blocks as role=tool messages, which must directly follow the assistant tool calls.</summary>
        private static void AddToolResults(JArray messages, JToken content)
        {
            foreach (var b in Arr(content))
            {
                if ((string)b["type"] != "tool_result") continue;
                var resultText = BlocksText(b["content"]);
                if (b.Value<bool?>("is_error") == true) resultText = "Error: " + resultText;
                messages.Add(new JObject { { "role", "tool" }, { "tool_call_id", (string)b["tool_use_id"] }, { "content", resultText } });
            }
        }

        private static string SystemText(JToken system)
        {
            return BlocksText(system);
        }

        public static IEnumerable<JToken> Arr(JToken t)
        {
            var a = t as JArray;
            return a != null ? (IEnumerable<JToken>)a : Enumerable.Empty<JToken>();
        }

        private static void Copy(JObject from, JObject to, string name)
        {
            var v = from[name];
            if (v != null && v.Type != JTokenType.Null) to[name] = v;
        }
    }

    /// <summary>OpenAI chat.completion.chunk objects in, Anthropic message stream events out.</summary>
    public sealed class OpenAiToAnthropicStream
    {
        private readonly string _model;
        private readonly string _id = "msg_" + Guid.NewGuid().ToString("N").Substring(0, 24);
        private bool _started;
        private bool _finished;
        private int _blockIndex = -1;
        private string _openBlock; // "text" or "tool:<openai index>"
        private readonly Dictionary<int, int> _toolBlocks = new Dictionary<int, int>();
        private string _finish;
        private bool _sawTool;

        public OpenAiToAnthropicStream(string model)
        {
            Usage = new TokenUsage();
            Text = new StringBuilder();
            _model = model;
        }

        public TokenUsage Usage { get; private set; }
        public StringBuilder Text { get; private set; }

        public IEnumerable<string> Feed(JObject chunk)
        {
            var output = new List<string>();
            Start(output);
            if (chunk["usage"] != null && chunk["usage"].Type == JTokenType.Object) Usage.Merge(TokenUsage.FromOpenAi(chunk["usage"]));
            foreach (var choice in Translate.Arr(chunk["choices"]))
            {
                var delta = choice["delta"];
                var text = (string)delta.At("content");
                if (!string.IsNullOrEmpty(text))
                {
                    if (_openBlock != "text")
                    {
                        CloseBlock(output);
                        _blockIndex++;
                        _openBlock = "text";
                        output.Add(SseWriter.Named("content_block_start", new JObject { { "type", "content_block_start" }, { "index", _blockIndex }, { "content_block", new JObject { { "type", "text" }, { "text", string.Empty } } } }));
                    }
                    Text.Append(text);
                    output.Add(SseWriter.Named("content_block_delta", new JObject { { "type", "content_block_delta" }, { "index", _blockIndex }, { "delta", new JObject { { "type", "text_delta" }, { "text", text } } } }));
                }
                foreach (var call in Translate.Arr(delta.At("tool_calls")))
                {
                    var idx = (int?)call["index"] ?? 0;
                    var key = "tool:" + idx;
                    if (!_toolBlocks.ContainsKey(idx))
                    {
                        CloseBlock(output);
                        _blockIndex++;
                        _toolBlocks[idx] = _blockIndex;
                        _openBlock = key;
                        _sawTool = true;
                        output.Add(SseWriter.Named("content_block_start", new JObject
                        {
                            { "type", "content_block_start" },
                            { "index", _blockIndex },
                            { "content_block", new JObject
                            {
                                { "type", "tool_use" },
                                { "id", (string)call["id"] ?? ("toolu_" + Guid.NewGuid().ToString("N").Substring(0, 20)) },
                                { "name", (string)call["function"].At("name") ?? "tool" },
                                { "input", new JObject() },
                            } },
                        }));
                    }
                    var args = (string)call["function"].At("arguments");
                    if (!string.IsNullOrEmpty(args) && _openBlock == key)
                    {
                        output.Add(SseWriter.Named("content_block_delta", new JObject { { "type", "content_block_delta" }, { "index", _toolBlocks[idx] }, { "delta", new JObject { { "type", "input_json_delta" }, { "partial_json", args } } } }));
                    }
                }
                var finish = (string)choice["finish_reason"];
                if (!string.IsNullOrEmpty(finish)) _finish = finish;
            }
            return output;
        }

        /// <summary>Closes the message. Safe to call more than once.</summary>
        public IEnumerable<string> Finish()
        {
            var output = new List<string>();
            if (_finished) return output;
            Start(output);
            CloseBlock(output);
            _finished = true;
            output.Add(SseWriter.Named("message_delta", new JObject
            {
                { "type", "message_delta" },
                { "delta", new JObject { { "stop_reason", Translate.StopReasonToAnthropic(_finish, _sawTool) }, { "stop_sequence", null } } },
                { "usage", DeltaUsage() },
            }));
            output.Add(SseWriter.Named("message_stop", new JObject { { "type", "message_stop" } }));
            return output;
        }

        // OpenAI reports prompt tokens only in the last chunk, after message_start went out with 0, so the final
        // message_delta carries them too: Anthropic clients (Claude Code's context and cost meters) take its usage.
        private JObject DeltaUsage()
        {
            var usage = new JObject();
            if (Usage.Input.HasValue) usage["input_tokens"] = Usage.Input.Value;
            usage["output_tokens"] = Usage.Output ?? 0;
            return usage;
        }

        private void Start(List<string> output)
        {
            if (_started) return;
            _started = true;
            output.Add(SseWriter.Named("message_start", new JObject
            {
                { "type", "message_start" },
                { "message", new JObject
                {
                    { "id", _id },
                    { "type", "message" },
                    { "role", "assistant" },
                    { "model", _model },
                    { "content", new JArray() },
                    { "stop_reason", null },
                    { "stop_sequence", null },
                    { "usage", new JObject { { "input_tokens", 0 }, { "output_tokens", 0 } } },
                } },
            }));
        }

        private void CloseBlock(List<string> output)
        {
            if (_openBlock == null) return;
            output.Add(SseWriter.Named("content_block_stop", new JObject { { "type", "content_block_stop" }, { "index", _blockIndex } }));
            _openBlock = null;
        }
    }

    /// <summary>Anthropic message stream events in, OpenAI chat.completion.chunk lines out.</summary>
    public sealed class AnthropicToOpenAiStream
    {
        private readonly string _model;
        private readonly bool _includeUsage;
        private readonly string _id = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 24);
        private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly Dictionary<int, int> _toolIndexByBlock = new Dictionary<int, int>();
        private bool _done;

        public AnthropicToOpenAiStream(string model, bool includeUsage)
        {
            Usage = new TokenUsage();
            Text = new StringBuilder();
            _model = model;
            _includeUsage = includeUsage;
        }

        public TokenUsage Usage { get; private set; }
        public StringBuilder Text { get; private set; }
        public string Error { get; private set; }

        public IEnumerable<string> Feed(string name, JObject data)
        {
            var output = new List<string>();
            var type = (string)data["type"] ?? name;
            switch (type)
            {
                case "message_start":
                    Usage.Merge(TokenUsage.FromAnthropic(data["message"].At("usage")));
                    output.Add(Chunk(new JObject { { "role", "assistant" }, { "content", string.Empty } }, null));
                    break;
                case "content_block_start":
                    {
                        var block = data["content_block"];
                        if ((string)block.At("type") == "tool_use")
                        {
                            var blockIndex = (int?)data["index"] ?? 0;
                            var toolIndex = _toolIndexByBlock.Count;
                            _toolIndexByBlock[blockIndex] = toolIndex;
                            output.Add(Chunk(new JObject
                            {
                                { "tool_calls", new JArray
                                {
                                    new JObject { { "index", toolIndex }, { "id", (string)block.At("id") }, { "type", "function" }, { "function", new JObject { { "name", (string)block.At("name") }, { "arguments", string.Empty } } } },
                                } },
                            }, null));
                        }
                        break;
                    }
                case "content_block_delta":
                    {
                        var delta = data["delta"];
                        var dtype = (string)delta.At("type");
                        if (dtype == "text_delta")
                        {
                            var text = (string)delta.At("text") ?? string.Empty;
                            Text.Append(text);
                            output.Add(Chunk(new JObject { { "content", text } }, null));
                        }
                        else if (dtype == "input_json_delta")
                        {
                            int toolIndex;
                            if (_toolIndexByBlock.TryGetValue((int?)data["index"] ?? 0, out toolIndex))
                            {
                                output.Add(Chunk(new JObject
                                {
                                    { "tool_calls", new JArray { new JObject { { "index", toolIndex }, { "function", new JObject { { "arguments", (string)delta.At("partial_json") ?? string.Empty } } } } } },
                                }, null));
                            }
                        }
                        break;
                    }
                case "message_delta":
                    {
                        Usage.Merge(TokenUsage.FromAnthropic(data["usage"]));
                        var stop = (string)data["delta"].At("stop_reason");
                        if (stop != null) output.Add(Chunk(new JObject(), Translate.StopReasonToOpenAi(stop)));
                        break;
                    }
                case "message_stop":
                    output.AddRange(Finish());
                    break;
                case "error":
                    Error = (string)data["error"].At("message") ?? "Upstream error";
                    output.Add(SseWriter.Data(Translate.Error(Dialect.OpenAi, 502, Error)));
                    output.AddRange(Finish());
                    break;
            }
            return output;
        }

        public IEnumerable<string> Finish()
        {
            var output = new List<string>();
            if (_done) return output;
            _done = true;
            if (_includeUsage)
            {
                output.Add(SseWriter.Data(new JObject
                {
                    { "id", _id },
                    { "object", "chat.completion.chunk" },
                    { "created", _created },
                    { "model", _model },
                    { "choices", new JArray() },
                    { "usage", new JObject { { "prompt_tokens", Usage.Input ?? 0 }, { "completion_tokens", Usage.Output ?? 0 }, { "total_tokens", (Usage.Input ?? 0) + (Usage.Output ?? 0) } } },
                }));
            }
            output.Add(SseWriter.Done);
            return output;
        }

        private string Chunk(JObject delta, string finish)
        {
            return SseWriter.Data(new JObject
            {
                { "id", _id },
                { "object", "chat.completion.chunk" },
                { "created", _created },
                { "model", _model },
                { "choices", new JArray { new JObject { { "index", 0 }, { "delta", delta }, { "finish_reason", finish } } } },
            });
        }
    }
}
