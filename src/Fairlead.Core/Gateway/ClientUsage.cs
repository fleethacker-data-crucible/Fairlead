using System;
using System.Collections.Generic;
using Fairlead.Core.Telemetry;

namespace Fairlead.Core.Gateway
{
    /// <summary>
    /// Tokens (input plus output, as the providers reported them) each client key has used today, UTC. Fed by every
    /// recorded request and seeded from today's request log at start, so a restart does not give a key a fresh budget.
    /// A request that is still running is not counted until it ends, so a key can overshoot its limit by what one
    /// request in flight uses.
    /// </summary>
    public sealed class ClientUsage
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, long> _tokens = new Dictionary<string, long>(StringComparer.Ordinal);
        private DateTime _day = DateTime.MinValue;

        public long Used(string clientId, DateTime nowUtc)
        {
            lock (_lock)
            {
                Roll(nowUtc);
                long v;
                return !string.IsNullOrEmpty(clientId) && _tokens.TryGetValue(clientId, out v) ? v : 0;
            }
        }

        public void Add(string clientId, long tokens, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(clientId) || tokens <= 0) return;
            lock (_lock)
            {
                Roll(nowUtc);
                long v;
                _tokens.TryGetValue(clientId, out v);
                _tokens[clientId] = v + tokens;
            }
        }

        /// <summary>Adds what the log says was already used today.</summary>
        public void Seed(IEnumerable<RequestLogEntry> today, DateTime nowUtc)
        {
            foreach (var e in today) Add(e.ClientId, (long)(e.InputTokens ?? 0) + (e.OutputTokens ?? 0), nowUtc);
        }

        /// <summary>Whole seconds until 00:00 UTC, when every key's day starts again.</summary>
        public static int SecondsToReset(DateTime nowUtc)
        {
            return Math.Max(1, (int)Math.Ceiling((nowUtc.Date.AddDays(1) - nowUtc).TotalSeconds));
        }

        private void Roll(DateTime nowUtc)
        {
            if (nowUtc.Date == _day) return;
            _day = nowUtc.Date;
            _tokens.Clear();
        }
    }
}
