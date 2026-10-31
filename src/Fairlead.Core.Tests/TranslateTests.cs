using System.Linq;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    public class TranslateTests
    {
        [Fact]
        public void AnthropicRequestBecomesOpenAiWithSystemToolsAndToolResultsInOrder()
        {
            var a = JObject.Parse(@"{
              ""model"": ""claude-x"", ""max_tokens"": 256, ""system"": [{""type"":""text"",""text"":""Be brief.""}],
              ""tools"": [{""name"":""get_time"",""description"":""now"",""input_schema"":{""type"":""object""}}, {""type"":""web_search_20250305"",""name"":""web_search""}],
              ""tool_choice"": {""type"":""any""},
              ""messages"": [
                {""role"":""user"",""content"":""What time is it?""},
                {""role"":""assistant"",""content"":[{""type"":""text"",""text"":""Checking.""},{""type"":""tool_use"",""id"":""t1"",""name"":""get_time"",""input"":{}}]},
                {""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""t1"",""content"":""12:00""},{""type"":""text"",""text"":""thanks""}]}
              ]}");
            var o = Translate.AnthropicToOpenAiRequest(a, "phi4", false, true);
            Assert.Equal("phi4", (string?)o["model"]);
            Assert.Equal(256, (int)o["max_tokens"]!);
            var msgs = (JArray)o["messages"]!;
            Assert.Equal("system", (string?)msgs[0]["role"]);
            Assert.Equal("Be brief.", (string?)msgs[0]["content"]);
            Assert.Equal("assistant", (string?)msgs[2]["role"]);
            Assert.Equal("get_time", (string?)msgs[2]["tool_calls"]![0]!["function"]!["name"]);
            Assert.Equal("tool", (string?)msgs[3]["role"]);
            Assert.Equal("t1", (string?)msgs[3]["tool_call_id"]);
            Assert.Equal("user", (string?)msgs[4]["role"]);
            Assert.Single((JArray)o["tools"]!); // the server tool has no OpenAI equivalent
            Assert.Equal("required", (string?)o["tool_choice"]);
        }

        [Fact]
        public void OpenAiBodyForOpenAiUsesMaxCompletionTokensAndDropsTemperatureOnReasoningModels()
        {
            var o = JObject.Parse(@"{""model"":""x"",""max_tokens"":700,""temperature"":0.2,""top_p"":0.9,""messages"":[]}");
            Translate.AdaptForOpenAi(o, "gpt-5-mini");
            Assert.Null(o["max_tokens"]);
            Assert.Equal(700, (int)o["max_completion_tokens"]!);
            Assert.Null(o["temperature"]);
            Assert.Null(o["top_p"]);

            var chat = JObject.Parse(@"{""max_tokens"":300,""temperature"":0.2,""top_p"":0.9}");
            Translate.AdaptForOpenAi(chat, "gpt-4.1-mini");
            Assert.Equal(300, (int)chat["max_completion_tokens"]!);
            Assert.Equal(0.2, (double)chat["temperature"]!, 3); // non-reasoning models keep their sampling settings
            Assert.NotNull(chat["top_p"]);

            var both = JObject.Parse(@"{""max_tokens"":1,""max_completion_tokens"":2}");
            Translate.AdaptForOpenAi(both, "gpt-4o");
            Assert.Null(both["max_tokens"]);
            Assert.Equal(2, (int)both["max_completion_tokens"]!); // an explicit max_completion_tokens wins

            var none = JObject.Parse(@"{""messages"":[]}");
            Translate.AdaptForOpenAi(none, "o4-mini");
            Assert.Null(none["max_completion_tokens"]);
        }

        [Theory]
        [InlineData("gpt-5", true)]
        [InlineData("gpt-5-mini", true)]
        [InlineData("GPT-5.4-2026-03-05", true)]
        [InlineData("o1", true)]
        [InlineData("o3-mini", true)]
        [InlineData("o4-mini-2025-04-16", true)]
        [InlineData("gpt-5-chat-latest", false)]
        [InlineData("gpt-4o", false)]
        [InlineData("gpt-4.1", false)]
        [InlineData("omni-moderation", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ReasoningModelsAreRecognized(string? model, bool expected)
        {
            Assert.Equal(expected, Translate.IsOpenAiReasoningModel(model!));
        }

        [Fact]
        public void OpenAiRequestBecomesAnthropicWithMergedTurnsAndDefaults()
        {
            var o = JObject.Parse(@"{
              ""model"": ""gpt-x"", ""temperature"": 1.6, ""stop"": ""END"",
              ""messages"": [
                {""role"":""system"",""content"":""Rules.""},
                {""role"":""user"",""content"":[{""type"":""text"",""text"":""hi""},{""type"":""image_url"",""image_url"":{""url"":""data:image/png;base64,AAAA""}}]},
                {""role"":""assistant"",""content"":null,""tool_calls"":[{""id"":""c1"",""type"":""function"",""function"":{""name"":""f"",""arguments"":""{\""a\"":1}""}},{""id"":""c2"",""type"":""function"",""function"":{""name"":""g"",""arguments"":""{}""}}]},
                {""role"":""tool"",""tool_call_id"":""c1"",""content"":""one""},
                {""role"":""tool"",""tool_call_id"":""c2"",""content"":""two""}
              ],
              ""tools"":[{""type"":""function"",""function"":{""name"":""f"",""parameters"":{""type"":""object""}}}]}");
            var a = Translate.OpenAiToAnthropicRequest(o, "claude-y");
            Assert.Equal("Rules.", (string?)a["system"]);
            Assert.Equal(4096, (int)a["max_tokens"]!);
            Assert.Equal(1.0, (double)a["temperature"]!);
            Assert.Equal("END", (string?)a["stop_sequences"]![0]);
            var msgs = (JArray)a["messages"]!;
            Assert.Equal(3, msgs.Count);
            Assert.Equal("base64", (string?)msgs[0]["content"]![1]!["source"]!["type"]);
            Assert.Equal(2, ((JArray)msgs[1]["content"]!).Count);
            Assert.Equal(1, (int)msgs[1]["content"]![0]!["input"]!["a"]!);
            var results = (JArray)msgs[2]["content"]!;
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal("tool_result", (string?)r["type"]));
        }

        [Fact]
        public void ResponsesTranslateBothWays()
        {
            var openai = FakeUpstream.OpenAiAnswer("hello");
            var asAnthropic = Translate.OpenAiToAnthropicResponse(openai, "claude-x");
            Assert.Equal("message", (string?)asAnthropic["type"]);
            Assert.Equal("hello", (string?)asAnthropic["content"]![0]!["text"]);
            Assert.Equal("end_turn", (string?)asAnthropic["stop_reason"]);
            Assert.Equal(11, (int)asAnthropic["usage"]!["input_tokens"]!);

            var anthropic = JObject.Parse(@"{""content"":[{""type"":""text"",""text"":""a""},{""type"":""tool_use"",""id"":""t"",""name"":""f"",""input"":{""x"":2}}],""stop_reason"":""tool_use"",""usage"":{""input_tokens"":5,""cache_read_input_tokens"":10,""output_tokens"":1}}");
            var asOpenAi = Translate.AnthropicToOpenAiResponse(anthropic, "gpt");
            Assert.Equal("tool_calls", (string?)asOpenAi["choices"]![0]!["finish_reason"]);
            Assert.Equal("{\"x\":2}", (string?)asOpenAi["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]);
            Assert.Equal(15, (int)asOpenAi["usage"]!["prompt_tokens"]!);
        }

        [Fact]
        public void OpenAiStreamBecomesAnthropicEventsWithToolBlocks()
        {
            var s = new OpenAiToAnthropicStream("claude-x");
            var events = s.Feed(JObject.Parse(@"{""choices"":[{""delta"":{""content"":""Hi""}}]}")).ToList();
            events.AddRange(s.Feed(JObject.Parse(@"{""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""id"":""c1"",""function"":{""name"":""f"",""arguments"":""{\""a\""""}}]}}]}")));
            events.AddRange(s.Feed(JObject.Parse(@"{""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""function"":{""arguments"":"":1}""}}]},""finish_reason"":""tool_calls""}]}")));
            events.AddRange(s.Feed(JObject.Parse(@"{""choices"":[],""usage"":{""prompt_tokens"":4,""completion_tokens"":7}}")));
            events.AddRange(s.Finish());
            var names = events.Select(e => e.Substring(7, e.IndexOf('\n') - 7)).ToList();
            Assert.Equal(new[] { "message_start", "content_block_start", "content_block_delta", "content_block_stop", "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop" }, names);
            Assert.Contains("\"stop_reason\":\"tool_use\"", events[8]);
            Assert.Contains("\"output_tokens\":7", events[8]);
            Assert.Contains("\"input_tokens\":4", events[8]); // message_start went out before the usage chunk
            Assert.Equal("Hi", s.Text.ToString());
        }

        [Fact]
        public void SystemMessagesInsideTheConversationBecomeUserTurnsAfterToolResults()
        {
            // The shape Claude Code sends: system notes between turns, one of them between a tool call and its result.
            var a = JObject.Parse(@"{
              ""model"": ""claude-x"", ""max_tokens"": 64, ""system"": ""Be brief."",
              ""messages"": [
                {""role"":""user"",""content"":[{""type"":""text"",""text"":""Read notes.txt""}]},
                {""role"":""system"",""content"":""Plan mode is off.""},
                {""role"":""system"",""content"":""""},
                {""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""t1"",""name"":""Read"",""input"":{}}]},
                {""role"":""system"",""content"":[{""type"":""text"",""text"":""<total_tokens>99 left</total_tokens>""}]},
                {""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""t1"",""content"":""line one""}]}
              ]}");
            var msgs = (JArray)Translate.AnthropicToOpenAiRequest(a, "phi4", false, true)["messages"]!;
            Assert.Equal(new[] { "system", "user", "user", "assistant", "tool", "user" }, msgs.Select(m => (string?)m["role"]).ToArray());
            Assert.Equal("Plan mode is off.", (string?)msgs[2]["content"]);
            Assert.Equal("t1", (string?)msgs[4]["tool_call_id"]);
            Assert.Equal("<total_tokens>99 left</total_tokens>", (string?)msgs[5]["content"]);
        }

        [Fact]
        public void AnthropicStreamBecomesOpenAiChunksAndDone()
        {
            var s = new AnthropicToOpenAiStream("gpt", true);
            var lines = s.Feed("message_start", JObject.Parse(@"{""type"":""message_start"",""message"":{""usage"":{""input_tokens"":3}}}")).ToList();
            lines.AddRange(s.Feed("content_block_start", JObject.Parse(@"{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""text"",""text"":""""}}")));
            lines.AddRange(s.Feed("content_block_delta", JObject.Parse(@"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""yo""}}")));
            lines.AddRange(s.Feed("message_delta", JObject.Parse(@"{""type"":""message_delta"",""delta"":{""stop_reason"":""end_turn""},""usage"":{""output_tokens"":2}}")));
            lines.AddRange(s.Feed("message_stop", JObject.Parse(@"{""type"":""message_stop""}")));
            Assert.Contains("\"content\":\"yo\"", lines[1]);
            Assert.Contains("\"finish_reason\":\"stop\"", lines[2]);
            Assert.Contains("\"prompt_tokens\":3", lines[3]);
            Assert.Equal(SseWriter.Done, lines.Last());
        }

        [Fact]
        public void SseReaderJoinsDataLinesAndSkipsComments()
        {
            var text = ": keepalive\n\nevent: ping\ndata: {\"a\":1}\n\ndata: line1\ndata: line2\n\ndata: [DONE]\n\n";
            using (var reader = new SseReader(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))))
            {
                var e1 = reader.NextAsync(default).Result!;
                Assert.Equal("ping", e1.Name);
                Assert.Equal(1, (int)e1.Json()!["a"]!);
                Assert.Equal("line1\nline2", reader.NextAsync(default).Result!.Data);
                Assert.True(reader.NextAsync(default).Result!.IsDone);
                Assert.Null(reader.NextAsync(default).Result);
            }
        }
    }
}
