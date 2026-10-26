using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fairlead.Core.Protocol;
using Fairlead.Core.JsonLite;

namespace Fairlead.Core.Telemetry
{
    public sealed class AttemptRecord
    {
        private string _target = string.Empty;
        public string Target { get { return _target; } set { _target = value; } }
        private string _outcome = string.Empty;
        public string Outcome { get { return _outcome; } set { _outcome = value; } } // ok, network, timeout, server, ratelimited, auth, modelnotfound, badrequest, skipped, cancelled, lost-race
        public int? Status { get; set; }
        public int Ms { get; set; }
        public string Error { get; set; }
    }

    /// <summary>One gateway request as the activity log shows it. No prompt text unless content logging is on.</summary>
    public sealed class RequestRecord
    {
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        private DateTimeOffset _time = DateTimeOffset.UtcNow;
        public DateTimeOffset Time { get { return _time; } set { _time = value; } }
        public string Client { get; set; }
        private string _dialect = string.Empty;
        public string Dialect { get { return _dialect; } set { _dialect = value; } }
        private string _endpoint = string.Empty;
        public string Endpoint { get { return _endpoint; } set { _endpoint = value; } }
        private string _requestedModel = string.Empty;
        public string RequestedModel { get { return _requestedModel; } set { _requestedModel = value; } }
        public string Route { get; set; }
        /// <summary>Plain-language trace of how the route and target were chosen.</summary>
        private List<string> _decision = new List<string>();
        public List<string> Decision { get { return _decision; } set { _decision = value; } }
        private List<AttemptRecord> _attempts = new List<AttemptRecord>();
        public List<AttemptRecord> Attempts { get { return _attempts; } set { _attempts = value; } }
        public string Target { get; set; }
        public bool Local { get; set; }
        public bool Kedge { get; set; }
        public bool Sensitive { get; set; }
        public bool Failover { get; set; }
        public bool Stream { get; set; }
        public int Status { get; set; }
        public string Error { get; set; }
        public int? FirstByteMs { get; set; }
        public int TotalMs { get; set; }
        public int EstimatedTokens { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        /// <summary>USD, only when the operator entered prices for the model.</summary>
        public decimal? Cost { get; set; }
        public string PromptPreview { get; set; }
        public string ResponsePreview { get; set; }
    }

    public sealed class TargetStats
    {
        private string _target = string.Empty;
        public string Target { get { return _target; } set { _target = value; } }
        public long Requests { get; set; }
        public long Failures { get; set; }
        /// <summary>Exponentially weighted time to first byte; drives the "fastest" strategy.</summary>
        public double? EwmaFirstByteMs { get; set; }
        public double? EwmaTotalMs { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal Cost { get; set; }
        public DateTimeOffset? LastUsed { get; set; }
    }

    /// <summary>In-memory activity log, per-target stats and totals, plus a redacted JSONL trail in logs\.</summary>
    public sealed class Metrics
    {
        private const double Alpha = 0.3;
        private readonly object _lock = new object();
        private readonly LinkedList<RequestRecord> _recent = new LinkedList<RequestRecord>();
        private readonly ConcurrentDictionary<string, TargetStats> _targets = new ConcurrentDictionary<string, TargetStats>(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;
        private readonly int _capacity;
        private long _total;
        private long _failed;
        private long _failovers;
        private long _local;
        private long _kedge;
        private long _sensitive;

        public Metrics(string logDirectory, int capacity = 1000)
        {
            _capacity = capacity;
            if (!string.IsNullOrEmpty(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    _file = Path.Combine(logDirectory, "fairlead-requests-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd") + ".jsonl");
                }
                catch (IOException) { _file = null; }
                catch (UnauthorizedAccessException) { _file = null; }
            }
        }

        private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset StartedAt { get { return _startedAt; } }

        public void Record(RequestRecord r)
        {
            lock (_lock)
            {
                _recent.AddFirst(r);
                while (_recent.Count > _capacity) _recent.RemoveLast();
                _total++;
                if (r.Status >= 400) _failed++;
                if (r.Failover) _failovers++;
                if (r.Local && r.Status < 400) _local++;
                if (r.Kedge) _kedge++;
                if (r.Sensitive) _sensitive++;
                if (_file != null)
                {
                    try
                    {
                        // Previews never go to disk, even with content logging on.
                        var preview = r.PromptPreview;
                        var answer = r.ResponsePreview;
                        r.PromptPreview = null;
                        r.ResponsePreview = null;
                        File.AppendAllText(_file, JsonConvert.SerializeObject(r, Host.Json.Settings) + "\n", new UTF8Encoding(false));
                        r.PromptPreview = preview;
                        r.ResponsePreview = answer;
                    }
                    catch (IOException) { }
                }
            }
            foreach (var a in r.Attempts)
            {
                if (a.Outcome == "skipped" || a.Outcome == "lost-race" || a.Outcome == "cancelled") continue;
                var s = _targets.GetOrAdd(a.Target, t => new TargetStats { Target = t });
                lock (s)
                {
                    s.Requests++;
                    s.LastUsed = r.Time;
                    if (a.Outcome != "ok") { s.Failures++; continue; }
                    if (a.Target == r.Target)
                    {
                        if (r.FirstByteMs.HasValue) s.EwmaFirstByteMs = s.EwmaFirstByteMs.HasValue ? (Alpha * r.FirstByteMs.Value) + ((1 - Alpha) * s.EwmaFirstByteMs.Value) : r.FirstByteMs.Value;
                        s.EwmaTotalMs = s.EwmaTotalMs.HasValue ? (Alpha * r.TotalMs) + ((1 - Alpha) * s.EwmaTotalMs.Value) : r.TotalMs;
                        s.InputTokens += r.InputTokens ?? 0;
                        s.OutputTokens += r.OutputTokens ?? 0;
                        s.Cost += r.Cost ?? 0m;
                    }
                }
            }
        }

        public TargetStats Stats(string target)
        {
            TargetStats s;
            return _targets.TryGetValue(target, out s) ? s : null;
        }

        public IReadOnlyList<RequestRecord> Recent(int count)
        {
            lock (_lock) return _recent.Take(count).ToList();
        }

        public RequestRecord Find(string id)
        {
            lock (_lock) return _recent.FirstOrDefault(r => r.Id == id);
        }

        public object Summary()
        {
            lock (_lock)
            {
                var window = _recent.Where(r => r.Time > DateTimeOffset.UtcNow.AddMinutes(-60)).ToList();
                return new
                {
                    since = StartedAt,
                    total = _total,
                    failed = _failed,
                    failovers = _failovers,
                    servedLocally = _local,
                    kedge = _kedge,
                    sensitive = _sensitive,
                    lastHour = new
                    {
                        requests = window.Count,
                        failed = window.Count(r => r.Status >= 400),
                        inputTokens = window.Sum(r => (long)(r.InputTokens ?? 0)),
                        outputTokens = window.Sum(r => (long)(r.OutputTokens ?? 0)),
                        cost = window.Any(r => r.Cost.HasValue) ? window.Sum(r => r.Cost ?? 0m) : (decimal?)null,
                        p50Ms = Percentile(window.Where(r => r.Status < 400).Select(r => r.TotalMs).ToList(), 0.5),
                        p95Ms = Percentile(window.Where(r => r.Status < 400).Select(r => r.TotalMs).ToList(), 0.95),
                        p50FirstByteMs = Percentile(window.Where(r => r.FirstByteMs.HasValue).Select(r => r.FirstByteMs.Value).ToList(), 0.5),
                    },
                    targets = _targets.Values.OrderByDescending(t => t.Requests).ToList(),
                };
            }
        }

        private static int? Percentile(List<int> values, double p)
        {
            if (values.Count == 0) return null;
            values.Sort();
            var idx = (int)Math.Ceiling(p * values.Count) - 1;
            return values[Math.Max(0, Math.Min(values.Count - 1, idx))];
        }
    }
}
