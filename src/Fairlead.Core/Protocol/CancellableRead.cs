using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fairlead.Core.Protocol
{
    /// <summary>
    /// Waits for a read that cannot itself be cancelled (a stream read, a body read) but gives up when a token fires.
    /// The read is left to finish or fail on its own when the connection is disposed; the caller stops waiting for it.
    /// </summary>
    public static class CancellableRead
    {
        public static async Task Wait(Task read, CancellationToken token)
        {
            if (read.IsCompleted || !token.CanBeCanceled) return;
            var gate = new TaskCompletionSource<bool>();
            using (token.Register(() => gate.TrySetResult(true)))
            {
                var first = await Task.WhenAny(read, gate.Task).ConfigureAwait(false);
                if (first != read)
                {
                    // Make sure the abandoned read's failure is never reported as unobserved.
                    var ignored = read.ContinueWith(t => { var e = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(token);
                }
            }
        }
    }
}
