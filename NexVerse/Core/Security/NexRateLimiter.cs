// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;

namespace NexVerse.Core.Security
{
    public readonly struct NexRateLimitDecision
    {
        public bool Accepted { get; }
        public int Limit { get; }
        public int Remaining { get; }
        public long ResetAt { get; }
        public int RetryAfterSeconds { get; }

        public NexRateLimitDecision(
            bool accepted,
            int limit,
            int remaining,
            long resetAt,
            int retryAfterSeconds)
        {
            Accepted = accepted;
            Limit = limit;
            Remaining = remaining;
            ResetAt = resetAt;
            RetryAfterSeconds = retryAfterSeconds;
        }
    }

    public sealed class NexFixedWindowRateLimiter
    {
        private readonly ConcurrentDictionary<string, Bucket> m_Buckets =
            new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);

        public int Limit { get; }
        public int WindowSeconds { get; }

        public NexFixedWindowRateLimiter(int limit, int windowSeconds)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit));
            if (windowSeconds < 1)
                throw new ArgumentOutOfRangeException(nameof(windowSeconds));

            Limit = limit;
            WindowSeconds = windowSeconds;
        }

        public NexRateLimitDecision TryConsume(
            string key,
            long nowUnixSeconds)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Rate-limit key is required.", nameof(key));

            long windowStart =
                nowUnixSeconds - (nowUnixSeconds % WindowSeconds);
            long resetAt = windowStart + WindowSeconds;

            Bucket bucket = m_Buckets.GetOrAdd(
                key,
                _ => new Bucket(windowStart));

            lock (bucket.Sync)
            {
                if (bucket.WindowStart != windowStart)
                {
                    bucket.WindowStart = windowStart;
                    bucket.Count = 0;
                }

                if (bucket.Count >= Limit)
                {
                    int retryAfter = (int)Math.Max(1, resetAt - nowUnixSeconds);
                    return new NexRateLimitDecision(
                        false,
                        Limit,
                        0,
                        resetAt,
                        retryAfter);
                }

                bucket.Count++;
                int remaining = Math.Max(0, Limit - bucket.Count);

                return new NexRateLimitDecision(
                    true,
                    Limit,
                    remaining,
                    resetAt,
                    0);
            }
        }

        public int RemoveIdleBuckets(long olderThanWindowStart)
        {
            int removed = 0;

            foreach (var pair in m_Buckets)
            {
                Bucket bucket = pair.Value;
                bool stale;

                lock (bucket.Sync)
                    stale = bucket.WindowStart < olderThanWindowStart;

                if (stale && m_Buckets.TryRemove(pair.Key, out _))
                    removed++;
            }

            return removed;
        }

        private sealed class Bucket
        {
            public object Sync { get; } = new object();
            public long WindowStart;
            public int Count;

            public Bucket(long windowStart)
            {
                WindowStart = windowStart;
            }
        }
    }
}
