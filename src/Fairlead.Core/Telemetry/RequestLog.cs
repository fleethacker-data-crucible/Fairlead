using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Fairlead.Core.Config;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Telemetry
{
    /// <summary>One line of the request log, slimmed to who, what and which model served it. No prompt or response text.</summary>
    public sealed class RequestLogEntry
    {
        public string Id { get; set; }
        public DateTimeOffset Time { get; set; }
        public string Client { get; set; }
        public string ClientId { get; set; }
        public string KeyPrefix { get; set; }
        public string Label { get; set; }
        public string Endpoint { get; set; }
        public string Dialect { get; set; }
        public string RequestedModel { get; set; }
        public string Route { get; set; }
        /// <summary>provider/model that answered; empty when nothing did.</summary>
        public string ServedBy { get; set; }
        public string FallbackVia { get; set; }
        public bool Local { get; set; }
        public bool Stream { get; set; }
        public int Status { get; set; }
        public string Error { get; set; }
        public int? FirstByteMs { get; set; }
        public int TotalMs { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public decimal? Cost { get; set; }
        public bool Failover { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>Totals for one client, label, model or route.</summary>
    public sealed class RequestLogGroup
    {
        public string Name { get; set; }
        public int Requests { get; set; }
        public int Failed { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal? Cost { get; set; }
        public int? P50Ms { get; set; }
        public int? P95Ms { get; set; }
    }

    /// <summary>What to read from the request log. Empty fields match everything.</summary>
    public sealed class RequestLogQuery
    {
        public DateTimeOffset? Since { get; set; }
        public DateTimeOffset? Until { get; set; }
        public string Client { get; set; }
        public string Label { get; set; }
        /// <summary>Substring of the serving provider/model.</summary>
        public string Target { get; set; }
        public string Route { get; set; }
        /// <summary>"ok", "error", "4xx", "5xx" or an exact status code.</summary>
        public string Status { get; set; }
        public int Limit { get; set; }

        public RequestLogQuery()
        {
            Limit = 100;
        }
    }

    /// <summary>
    /// The durable request log: <c>fairlead-requests-yyyyMMdd.jsonl</c> files in the log folder, one line per request,
    /// written by <see cref="Metrics"/>. This is the read side, so the record survives a restart and can be filtered and
    /// totalled. Prompt and response text never reach these files; see <see cref="Metrics.Record"/>.
    /// </summary>
    public static class RequestLog
    {
        public const string FilePrefix = "fairlead-requests-";
        public const int MaxLabelLength = 64;
        public const int MaxLimit = 5000;
        private const int MaxFiles = 60;
        private const int MaxErrorLength = 200;

        public static string FileFor(string logDirectory, DateTimeOffset time)
        {
            return Path.Combine(logDirectory, FilePrefix + time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        /// <summary>Every entry in the file for one UTC day (no limit), for totals that must not miss any.</summary>
        public static List<RequestLogEntry> ReadDay(string logDirectory, DateTimeOffset day)
        {
            var result = new List<RequestLogEntry>();
            if (string.IsNullOrEmpty(logDirectory)) return result;
            var path = FileFor(logDirectory, day);
            if (!File.Exists(path)) return result;
            foreach (var line in ReadLines(path))
            {
                var entry = Parse(line);
                if (entry != null) result.Add(entry);
            }
            return result;
        }

        /// <summary>Names the caller on the record: the client key (id, visible prefix) and the cleaned label.</summary>
        public static void Identify(RequestRecord record, ClientKey client, string label)
        {
            if (client != null)
            {
                record.ClientId = client.Id;
                record.KeyPrefix = client.Prefix;
            }
            record.Label = CleanLabel(label);
        }

        /// <summary>A header value as a label: ASCII letters, digits and ._:/@#- only, at most 64 characters; null when empty.</summary>
        public static string CleanLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var sb = new StringBuilder();
            foreach (var ch in label.Trim())
            {
                if (sb.Length >= MaxLabelLength) break;
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == ' ' || ch == '.' || ch == '_' || ch == ':' || ch == '/' || ch == '@' || ch == '#' || ch == '-';
                sb.Append(ok ? ch : '_');
            }
            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        /// <summary>Newest first. Reads the daily files that can hold the window; a damaged line is skipped, not fatal.</summary>
        public static List<RequestLogEntry> Read(string logDirectory, RequestLogQuery query)
        {
            var result = new List<RequestLogEntry>();
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory)) return result;
            var limit = Math.Max(1, Math.Min(MaxLimit, query.Limit));
            var files = Directory.GetFiles(logDirectory, FilePrefix + "*.jsonl").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles);
            foreach (var file in files)
            {
                if (query.Since.HasValue && FileDay(file) < query.Since.Value.UtcDateTime.Date) break;
                foreach (var line in ReadLines(file))
                {
                    var entry = Parse(line);
                    if (entry != null && Matches(entry, query)) result.Add(entry);
                }
                if (result.Count >= limit) break;
            }
            result = result.OrderByDescending(e => e.Time).ToList();
            if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
            return result;
        }

        /// <summary>Totals for a set of entries, overall and per client, label, serving model and route.</summary>
        public static object Summarize(IList<RequestLogEntry> entries)
        {
            return new
            {
                requests = entries.Count,
                failed = entries.Count(e => e.Status >= 400),
                inputTokens = entries.Sum(e => (long)(e.InputTokens ?? 0)),
                outputTokens = entries.Sum(e => (long)(e.OutputTokens ?? 0)),
                cost = entries.Any(e => e.Cost.HasValue) ? entries.Sum(e => e.Cost ?? 0m) : (decimal?)null,
                from = entries.Count == 0 ? (DateTimeOffset?)null : entries.Min(e => e.Time),
                to = entries.Count == 0 ? (DateTimeOffset?)null : entries.Max(e => e.Time),
                byClient = Group(entries, e => string.IsNullOrEmpty(e.Client) ? "(no key)" : e.Client),
                byLabel = Group(entries.Where(e => !string.IsNullOrEmpty(e.Label)).ToList(), e => e.Label),
                byModel = Group(entries, e => string.IsNullOrEmpty(e.ServedBy) ? "(none)" : e.ServedBy),
                byRoute = Group(entries, e => string.IsNullOrEmpty(e.Route) ? "(none)" : e.Route),
            };
        }

        private static List<RequestLogGroup> Group(IList<RequestLogEntry> entries, Func<RequestLogEntry, string> key)
        {
            return entries.GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var ok = g.Where(e => e.Status < 400).Select(e => e.TotalMs).OrderBy(x => x).ToList();
                    return new RequestLogGroup
                    {
                        Name = g.Key,
                        Requests = g.Count(),
                        Failed = g.Count(e => e.Status >= 400),
                        InputTokens = g.Sum(e => (long)(e.InputTokens ?? 0)),
                        OutputTokens = g.Sum(e => (long)(e.OutputTokens ?? 0)),
                        Cost = g.Any(e => e.Cost.HasValue) ? g.Sum(e => e.Cost ?? 0m) : (decimal?)null,
                        P50Ms = ok.Count == 0 ? (int?)null : ok[Math.Max(0, (int)Math.Ceiling(0.5 * ok.Count) - 1)],
                        P95Ms = ok.Count == 0 ? (int?)null : ok[Math.Max(0, (int)Math.Ceiling(0.95 * ok.Count) - 1)],
                    };
                })
                .OrderByDescending(x => x.Requests)
                .ToList();
        }

        /// <summary>"90m", "24h", "7d" or an ISO date and time; null when empty or not understood.</summary>
        public static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            int n;
            if (text.Length > 1 && int.TryParse(text.Substring(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                var unit = char.ToLowerInvariant(text[text.Length - 1]);
                if (unit == 'm') return now.AddMinutes(-n);
                if (unit == 'h') return now.AddHours(-n);
                if (unit == 'd') return now.AddDays(-n);
            }
            DateTimeOffset when;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when)) return when;
            return null;
        }

        private static bool Matches(RequestLogEntry e, RequestLogQuery q)
        {
            if (q.Since.HasValue && e.Time < q.Since.Value) return false;
            if (q.Until.HasValue && e.Time > q.Until.Value) return false;
            if (!string.IsNullOrEmpty(q.Client) && !string.Equals(e.Client, q.Client, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(q.Label) && !string.Equals(e.Label, q.Label, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(q.Route) && !string.Equals(e.Route, q.Route, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(q.Target) && (e.ServedBy ?? string.Empty).IndexOf(q.Target, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrEmpty(q.Status))
            {
                var s = q.Status.Trim().ToLowerInvariant();
                int code;
                if (s == "ok") { if (e.Status >= 400) return false; }
                else if (s == "error") { if (e.Status < 400) return false; }
                else if (s.Length == 3 && s.EndsWith("xx", StringComparison.Ordinal)) { if (e.Status / 100 != s[0] - '0') return false; }
                else if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out code)) { if (e.Status != code) return false; }
            }
            return true;
        }

        private static DateTime FileDay(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            DateTime day;
            if (name.Length == FilePrefix.Length + 8 && DateTime.TryParseExact(name.Substring(FilePrefix.Length), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return day;
            return DateTime.MinValue;
        }

        private static IEnumerable<string> ReadLines(string file)
        {
            // The server may be appending while we read: open shared.
            var lines = new List<string>();
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) if (line.Length > 0) lines.Add(line);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return lines;
        }

        private static RequestLogEntry Parse(string line)
        {
            try
            {
                var o = JObject.Parse(line);
                var attempts = o["attempts"] as JArray;
                var error = (string)o["error"];
                if (error != null && error.Length > MaxErrorLength) error = error.Substring(0, MaxErrorLength) + "...";
                return new RequestLogEntry
                {
                    Id = (string)o["id"] ?? string.Empty,
                    Time = (DateTimeOffset?)o["time"] ?? DateTimeOffset.MinValue,
                    Client = (string)o["client"],
                    ClientId = (string)o["clientId"],
                    KeyPrefix = (string)o["keyPrefix"],
                    Label = (string)o["label"],
                    Endpoint = (string)o["endpoint"] ?? string.Empty,
                    Dialect = (string)o["dialect"] ?? string.Empty,
                    RequestedModel = (string)o["requestedModel"] ?? string.Empty,
                    Route = (string)o["route"],
                    ServedBy = (string)o["target"],
                    FallbackVia = (string)o["fallbackVia"],
                    Local = (bool?)o["local"] ?? false,
                    Stream = (bool?)o["stream"] ?? false,
                    Status = (int?)o["status"] ?? 0,
                    Error = error,
                    FirstByteMs = (int?)o["firstByteMs"],
                    TotalMs = (int?)o["totalMs"] ?? 0,
                    InputTokens = (int?)o["inputTokens"],
                    OutputTokens = (int?)o["outputTokens"],
                    Cost = (decimal?)o["cost"],
                    Failover = (bool?)o["failover"] ?? false,
                    Attempts = attempts == null ? 0 : attempts.Count,
                };
            }
            catch (JsonException) { return null; }
            catch (InvalidCastException) { return null; }
            catch (FormatException) { return null; }
        }
    }
}
