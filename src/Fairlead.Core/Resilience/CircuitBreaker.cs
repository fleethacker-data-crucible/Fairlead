using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Fairlead.Core.Providers;

namespace Fairlead.Core.Resilience
{
    public enum BreakerState { Closed, Open, HalfOpen }

    /// <summary>
    /// One breaker per provider (and one per provider/model for "model not found"). Closed: calls flow. Open: the
    /// target is skipped until the cooldown ends. Half-open: one trial call decides. Cooldowns double on repeated
    /// failure up to five minutes; a rate limit honours Retry-After; a bad key stays open until the key changes.
    /// </summary>
    public sealed class CircuitBreaker
    {
        private static readonly TimeSpan MinCooldown = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);
        private readonly object _lock = new object();
        private int _failures;
        private DateTimeOffset? _openUntil;
        private TimeSpan _cooldown = MinCooldown;
        private bool _trialInFlight;

        public CircuitBreaker(string key) { Key = key; }

        public string Key { get; private set; }
        public int Threshold { get; set; } = 3;
        public string? LastError { get; private set; }
        public FailureKind? LastFailure { get; private set; }
        public DateTimeOffset? LastFailureAt { get; private set; }
        public DateTimeOffset? LastSuccessAt { get; private set; }

        public BreakerState State(DateTimeOffset now)
        {
            lock (_lock)
            {
                if (_openUntil == null) return BreakerState.Closed;
                return now >= _openUntil.Value ? BreakerState.HalfOpen : BreakerState.Open;
            }
        }

        public DateTimeOffset? OpenUntil { get { lock (_lock) return _openUntil; } }

        /// <summary>True when a call may go out now. In half-open, only the first caller gets the trial.</summary>
        public bool TryAcquire(DateTimeOffset now)
        {
            lock (_lock)
            {
                if (_openUntil == null) return true;
                if (now < _openUntil.Value) return false;
                if (_trialInFlight) return false;
                _trialInFlight = true;
                return true;
            }
        }

        public void Success()
        {
            lock (_lock)
            {
                _failures = 0;
                _openUntil = null;
                _cooldown = MinCooldown;
                _trialInFlight = false;
                LastSuccessAt = DateTimeOffset.UtcNow;
            }
        }

        public void Failure(FailureKind kind, string message, TimeSpan? retryAfter = null)
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                LastError = message;
                LastFailure = kind;
                LastFailureAt = now;
                var wasTrial = _trialInFlight;
                _trialInFlight = false;
                switch (kind)
                {
                    case FailureKind.BadRequest:
                        return; // the request was wrong, not the target
                    case FailureKind.Auth:
                        _openUntil = now.AddHours(1);
                        return;
                    case FailureKind.RateLimited:
                        _openUntil = now + Clamp(retryAfter ?? TimeSpan.FromSeconds(30));
                        return;
                    case FailureKind.ModelNotFound:
                        _openUntil = now.AddMinutes(5);
                        return;
                }
                _failures++;
                // A refused or unreachable endpoint opens at once: waiting for three timeouts would cost minutes.
                if (wasTrial || kind == FailureKind.Network || _failures >= Threshold)
                {
                    if (wasTrial) _cooldown = TimeSpan.FromTicks(Math.Min(MaxCooldown.Ticks, _cooldown.Ticks * 2));
                    _openUntil = now + _cooldown;
                }
            }
        }

        /// <summary>Closes the breaker after a configuration change (a new key) or a passing probe.</summary>
        public void Reset()
        {
            Success();
            lock (_lock) LastSuccessAt = null;
        }

        private static TimeSpan Clamp(TimeSpan t)
        {
            if (t < TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
            return t > MaxCooldown ? MaxCooldown : t;
        }

        public object Describe(DateTimeOffset now)
        {
            lock (_lock)
            {
                return new
                {
                    key = Key,
                    state = (_openUntil == null ? BreakerState.Closed : now >= _openUntil.Value ? BreakerState.HalfOpen : BreakerState.Open).ToString().ToLowerInvariant(),
                    openUntil = _openUntil,
                    failures = _failures,
                    lastError = LastError,
                    lastFailure = LastFailure?.ToString(),
                    lastFailureAt = LastFailureAt,
                    lastSuccessAt = LastSuccessAt,
                };
            }
        }
    }

    public sealed class BreakerBoard
    {
        private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new ConcurrentDictionary<string, CircuitBreaker>(StringComparer.OrdinalIgnoreCase);

        public CircuitBreaker For(string key)
        {
            return _breakers.GetOrAdd(key, k => new CircuitBreaker(k));
        }

        public CircuitBreaker? Find(string key)
        {
            CircuitBreaker? b;
            return _breakers.TryGetValue(key, out b) ? b : null;
        }

        /// <summary>Resets the provider's breaker and every provider/model breaker under it.</summary>
        public void ResetProvider(string providerId)
        {
            foreach (var kv in _breakers)
            {
                if (string.Equals(kv.Key, providerId, StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase)) kv.Value.Reset();
            }
        }

        public IEnumerable<CircuitBreaker> All { get { return _breakers.Values.OrderBy(b => b.Key); } }
    }
}
