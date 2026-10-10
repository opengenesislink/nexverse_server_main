// SPDX-License-Identifier: MPL-2.0
using System;
using System.Threading;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>
    /// Per-viewer NavGraph read throttling. CompareExchange guarantees that
    /// only one simultaneous request can claim each time window. A simple
    /// check followed by Exchange is racy: two callers may both pass.
    /// </summary>
    public sealed class OglNavGraphRateGate
    {
        private long m_LastAccepted = long.MinValue;

        public bool TryAcquire(long monotonicMilliseconds, long intervalMilliseconds = 3000)
        {
            if (monotonicMilliseconds < 0 ||
                intervalMilliseconds < 1 || intervalMilliseconds > 60000)
                return false;
            while (true)
            {
                long last = Interlocked.Read(ref m_LastAccepted);
                if (last != long.MinValue)
                {
                    // Reject clock rollback rather than bypass the limiter.
                    if (monotonicMilliseconds < last ||
                        monotonicMilliseconds - last < intervalMilliseconds)
                        return false;
                }
                if (Interlocked.CompareExchange(
                        ref m_LastAccepted, monotonicMilliseconds, last) == last)
                    return true;
            }
        }
    }
}
