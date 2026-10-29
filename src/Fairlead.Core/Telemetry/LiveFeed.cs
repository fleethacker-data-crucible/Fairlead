using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fairlead.Core.Telemetry
{
    /// <summary>One request lifecycle event for the live routing view: start, attempt, firstByte or done.</summary>
    public sealed class LiveEvent
    {
        public string Type { get; set; }
        /// <summary>Already-serialized JSON payload; serialized once, sent to every subscriber.</summary>
        public string Json { get; set; }
    }

    /// <summary>
    /// Fan-out of request lifecycle events to the admin UI's live view (/api/live). In memory only, nothing on disk.
    /// Each subscriber has a bounded queue: a stalled browser loses events rather than holding memory, and the
    /// gateway never waits on a subscriber.
    /// </summary>
    public sealed class LiveFeed
    {
        private const int MaxQueued = 256;
        private readonly object _lock = new object();
        private readonly List<Subscription> _subs = new List<Subscription>();

        public int Subscribers { get { lock (_lock) return _subs.Count; } }

        public void Publish(string type, Func<string> json)
        {
            Subscription[] subs;
            lock (_lock)
            {
                if (_subs.Count == 0) return;
                subs = _subs.ToArray();
            }
            string payload;
            try { payload = json(); }
            catch (Exception) { return; }
            var ev = new LiveEvent { Type = type, Json = payload };
            foreach (var s in subs) s.Offer(ev);
        }

        public Subscription Subscribe()
        {
            var s = new Subscription(this);
            lock (_lock) _subs.Add(s);
            return s;
        }

        private void Remove(Subscription s)
        {
            lock (_lock) _subs.Remove(s);
        }

        public sealed class Subscription : IDisposable
        {
            private readonly LiveFeed _feed;
            private readonly ConcurrentQueue<LiveEvent> _queue = new ConcurrentQueue<LiveEvent>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private int _count;
            private int _dropped;

            internal Subscription(LiveFeed feed) { _feed = feed; }

            /// <summary>Events dropped because this subscriber fell behind; reset when read.</summary>
            public int TakeDropped() { return Interlocked.Exchange(ref _dropped, 0); }

            internal void Offer(LiveEvent ev)
            {
                if (Interlocked.Increment(ref _count) > MaxQueued)
                {
                    Interlocked.Decrement(ref _count);
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                _queue.Enqueue(ev);
                try { _signal.Release(); } catch (ObjectDisposedException) { }
            }

            /// <summary>The next event, or null when <paramref name="waitMs"/> passes with nothing to send.</summary>
            public async Task<LiveEvent> NextAsync(int waitMs, CancellationToken token)
            {
                if (!await _signal.WaitAsync(waitMs, token).ConfigureAwait(false)) return null;
                LiveEvent ev;
                if (!_queue.TryDequeue(out ev)) return null;
                Interlocked.Decrement(ref _count);
                return ev;
            }

            public void Dispose()
            {
                _feed.Remove(this);
                _signal.Dispose();
            }
        }
    }
}
