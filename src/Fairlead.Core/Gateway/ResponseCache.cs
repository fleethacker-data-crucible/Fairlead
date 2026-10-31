using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Gateway
{
    /// <summary>One remembered answer: the response body as it was sent, and where it came from.</summary>
    public sealed class CachedAnswer
    {
        public string Body { get; set; }
        public string Route { get; set; }
        public string Target { get; set; }
        public bool Local { get; set; }
        public DateTime StoredUtc { get; set; }
    }

    /// <summary>
    /// An exact-match cache for non-streaming, temperature-0 chat requests, in memory only. Off unless a lifetime is set
    /// (FAIRLEAD_RESPONSE_CACHE_SECONDS). The key is a hash of the client key, the dialect, the route header and the
    /// canonical request body (property order and spacing do not matter), so one key's answers are never served to another
    /// and a changed prompt, model or parameter is a different entry. Nothing is written to disk.
    /// </summary>
    public sealed class ResponseCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, CachedAnswer>>> _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, CachedAnswer>>>(StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, CachedAnswer>> _order = new LinkedList<KeyValuePair<string, CachedAnswer>>();
        private long _hits;
        private long _misses;

        public ResponseCache(int ttlSeconds, int maxEntries)
        {
            TtlSeconds = Math.Max(0, ttlSeconds);
            MaxEntries = maxEntries <= 0 ? 500 : maxEntries;
        }

        public int TtlSeconds { get; private set; }
        public int MaxEntries { get; private set; }
        public bool Enabled { get { return TtlSeconds > 0; } }

        public bool TryGet(string key, DateTime nowUtc, out CachedAnswer answer)
        {
            answer = null;
            lock (_lock)
            {
                LinkedListNode<KeyValuePair<string, CachedAnswer>> node;
                if (!_map.TryGetValue(key, out node)) { _misses++; return false; }
                if ((nowUtc - node.Value.Value.StoredUtc).TotalSeconds >= TtlSeconds)
                {
                    _order.Remove(node);
                    _map.Remove(key);
                    _misses++;
                    return false;
                }
                _order.Remove(node);
                _order.AddFirst(node);
                _hits++;
                answer = node.Value.Value;
                return true;
            }
        }

        public void Put(string key, CachedAnswer answer)
        {
            lock (_lock)
            {
                LinkedListNode<KeyValuePair<string, CachedAnswer>> old;
                if (_map.TryGetValue(key, out old)) { _order.Remove(old); _map.Remove(key); }
                var node = _order.AddFirst(new KeyValuePair<string, CachedAnswer>(key, answer));
                _map[key] = node;
                while (_order.Count > MaxEntries)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public int Clear()
        {
            lock (_lock)
            {
                var n = _order.Count;
                _order.Clear();
                _map.Clear();
                return n;
            }
        }

        public object Status()
        {
            lock (_lock) return new { enabled = Enabled, ttlSeconds = TtlSeconds, maxEntries = MaxEntries, entries = _order.Count, hits = _hits, misses = _misses };
        }

        /// <summary>Only a request that asks for the same answer every time may be cached: temperature 0, no streaming.</summary>
        public static bool IsDeterministic(JObject body)
        {
            var stream = body["stream"];
            if (stream != null && stream.Type == JTokenType.Boolean && (bool)stream) return false;
            var temperature = body["temperature"];
            if (temperature == null) return false;
            double t;
            try { t = (double)temperature; }
            catch (InvalidCastException) { return false; }
            catch (FormatException) { return false; }
            return t == 0.0;
        }

        public static string KeyFor(string clientId, Protocol.Dialect dialect, string routeHeader, JObject body)
        {
            var text = (clientId ?? string.Empty) + "\n" + dialect + "\n" + (routeHeader ?? string.Empty) + "\n" + Canonical(body);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The body with object properties in name order at every level, as compact JSON.</summary>
        public static string Canonical(JToken token)
        {
            var sb = new StringBuilder();
            Write(token, sb);
            return sb.ToString();
        }

        private static void Write(JToken token, StringBuilder sb)
        {
            var obj = token as JObject;
            var arr = token as JArray;
            if (obj != null)
            {
                var names = new List<string>();
                foreach (var p in obj.Properties()) names.Add(p.Name);
                names.Sort(StringComparer.Ordinal);
                sb.Append('{');
                for (var i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonConvert.SerializeObject(names[i])).Append(':');
                    Write(obj[names[i]], sb);
                }
                sb.Append('}');
            }
            else if (arr != null)
            {
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
            }
            else sb.Append(token == null ? "null" : token.ToString(Formatting.None));
        }
    }
}
