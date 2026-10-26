using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fairlead.Core.JsonLite;

namespace Fairlead.Core.Host
{
    public sealed class LogEntry
    {
        private DateTimeOffset _time = DateTimeOffset.UtcNow;
        public DateTimeOffset Time { get { return _time; } set { _time = value; } }
        private string _level = "INFO";
        public string Level { get { return _level; } set { _level = value; } }
        private string _event = string.Empty;
        public string Event { get { return _event; } set { _event = value; } }
        public string Message { get; set; }
        public string Route { get; set; }
        public string RequestId { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                { "time", Time.ToString("o") },
                { "level", Level },
                { "event", Event },
                { "message", Message },
                { "route", Route },
                { "requestId", RequestId },
            };
        }
    }

    /// <summary>
    /// In-memory ring buffer plus an append-only JSONL file per process. Messages are redacted of token-shaped
    /// strings before storage; nothing here ever receives a header value.
    /// </summary>
    public sealed class Diagnostics
    {
        private readonly object _lock = new object();
        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly int _capacity;
        private readonly string _file;

        public Diagnostics(string logDirectory, int capacity = 2000)
        {
            _capacity = capacity;
            if (!string.IsNullOrEmpty(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    _file = Path.Combine(logDirectory, "fairlead-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".jsonl");
                }
                catch (IOException) { _file = null; }
                catch (UnauthorizedAccessException) { _file = null; }
            }
        }

        public string FilePath { get { return _file; } }

        public event Action<LogEntry> Logged;

        public void Info(string evt, string message = null, string route = null, string requestId = null) { Write("INFO", evt, message, route, requestId); }
        public void Warn(string evt, string message = null, string route = null, string requestId = null) { Write("WARN", evt, message, route, requestId); }
        public void Error(string evt, string message = null, string route = null, string requestId = null) { Write("ERROR", evt, message, route, requestId); }

        public void Write(string level, string evt, string message, string route, string requestId)
        {
            var entry = new LogEntry { Level = level, Event = evt, Message = message == null ? null : Config.Redactor.Redact(message), Route = route, RequestId = requestId };
            lock (_lock)
            {
                _entries.Add(entry);
                if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
                if (_file != null)
                {
                    try { File.AppendAllText(_file, entry.ToJson().ToString(Formatting.None) + "\n", new UTF8Encoding(false)); }
                    catch (IOException) { }
                }
            }
            var handler = Logged;
            if (handler != null) handler(entry);
        }

        public IReadOnlyList<LogEntry> Recent(int count = 200, string level = null)
        {
            lock (_lock)
            {
                IEnumerable<LogEntry> q = _entries;
                if (!string.IsNullOrEmpty(level)) q = q.Where(e => string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase));
                return q.Reverse().Take(count).ToList();
            }
        }
    }
}
