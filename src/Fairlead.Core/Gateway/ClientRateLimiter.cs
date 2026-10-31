using System;
using System.Collections.Generic;

namespace Fairlead.Core.Gateway
{
    /// <summary>
    /// A sliding one-minute window of requests per client key, in memory. A key with no limit never reaches it. The window
    /// restarts with Fairlead, which is the point: it protects the models from a runaway tool, it is not accounting.
    /// </summary>
    public sealed class ClientRateLimiter
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        private readonly object _lock = new object();
        private readonly Dictionary<string, Queue<DateTime>> _hits = new Dictionary<string, Queue<DateTime>>(StringComparer.Ordinal);

        /// <summary>
        /// Counts this request and returns true when it is within <paramref name="limitPerMinute"/>. When it is not, nothing is
        /// counted and <paramref name="retryAfterSeconds"/> says when the oldest request leaves the window.
        /// </summary>
        public bool TryAcquire(string clientId, int limitPerMinute, DateTime nowUtc, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (limitPerMinute <= 0 || string.IsNullOrEmpty(clientId)) return true;
            lock (_lock)
            {
                Queue<DateTime> q;
                if (!_hits.TryGetValue(clientId, out q)) { q = new Queue<DateTime>(); _hits[clientId] = q; }
                while (q.Count > 0 && nowUtc - q.Peek() >= Window) q.Dequeue();
                if (q.Count >= limitPerMinute)
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((Window - (nowUtc - q.Peek())).TotalSeconds));
                    return false;
                }
                q.Enqueue(nowUtc);
                if (_hits.Count > 1000) Trim(nowUtc);
                return true;
            }
        }

        /// <summary>Forgets a key's window (its limit changed or it was revoked).</summary>
        public void Forget(string clientId)
        {
            lock (_lock) _hits.Remove(clientId);
        }

        private void Trim(DateTime nowUtc)
        {
            var dead = new List<string>();
            foreach (var kv in _hits)
                if (kv.Value.Count == 0 || nowUtc - kv.Value.Peek() >= Window) dead.Add(kv.Key);
            foreach (var k in dead) _hits.Remove(k);
        }
    }
}
