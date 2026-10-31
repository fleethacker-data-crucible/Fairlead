using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Protocol
{
    /// <summary>One server-sent event: the optional event name and the joined data lines.</summary>
    public sealed class SseEvent
    {
        public SseEvent(string name, string data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; private set; }
        public string Data { get; private set; }
        public bool IsDone { get { return Data == "[DONE]"; } }

        /// <summary>A comment line (": keep-alive"): no data, but proof the upstream is still alive.</summary>
        public bool IsComment { get; private set; }

        public static SseEvent Comment()
        {
            return new SseEvent(null, string.Empty) { IsComment = true };
        }

        /// <summary>The data as JSON, or null when it is not JSON ([DONE], comments, keep-alives).</summary>
        public JObject Json()
        {
            if (Data.Length == 0 || Data[0] != '{') return null;
            try { return JObject.Parse(Data); }
            catch (JsonException) { return null; }
        }

        /// <summary>The event exactly as it goes on the wire again.</summary>
        public string ToWire()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Name)) sb.Append("event: ").Append(Name).Append('\n');
            foreach (var line in Data.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>Reads server-sent events from an upstream response body, line by line.</summary>
    public sealed class SseReader : IDisposable
    {
        private readonly StreamReader _reader;

        /// <summary>When true, a comment line between events comes back as an event with IsComment set, so a caller can reset its idle timer and pass the keep-alive on. Off by default: comments are skipped.</summary>
        public bool EmitComments { get; set; }

        public SseReader(Stream stream)
        {
            _reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096);
        }

        /// <summary>The next event, or null at the end of the stream.</summary>
        public async Task<SseEvent> NextAsync(CancellationToken token)
        {
            string name = null;
            StringBuilder data = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    return data == null ? null : new SseEvent(name, data.ToString());
                }
                if (line.Length == 0)
                {
                    if (data != null) return new SseEvent(name, data.ToString());
                    name = null;
                    continue;
                }
                if (line[0] == ':')
                {
                    if (EmitComments && data == null) return SseEvent.Comment();
                    continue; // comment / keep-alive
                }
                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line.Substring(0, colon);
                var value = colon < 0 ? string.Empty : line.Substring(colon + 1);
                if (value.StartsWith(" ")) value = value.Substring(1);
                if (field == "event") name = value;
                else if (field == "data")
                {
                    if (data == null) data = new StringBuilder(value);
                    else data.Append('\n').Append(value);
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
        }
    }

    public static class SseWriter
    {
        public static string Data(JToken json)
        {
            return "data: " + json.ToString(Formatting.None) + "\n\n";
        }

        public static string Named(string name, JToken json)
        {
            return "event: " + name + "\ndata: " + json.ToString(Formatting.None) + "\n\n";
        }

        public const string Done = "data: [DONE]\n\n";

        /// <summary>An SSE comment: every client ignores it, and it keeps idle-timeout timers in between from firing.</summary>
        public const string KeepAlive = ": keep-alive\n\n";
    }
}
