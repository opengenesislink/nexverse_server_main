using System;
using NexVerse.Core.Security;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        NexFixedWindowRateLimiter limiter =
            new NexFixedWindowRateLimiter(3, 60);

        long t = 120;

        NexRateLimitDecision a = limiter.TryConsume("client-a", t);
        NexRateLimitDecision b = limiter.TryConsume("client-a", t + 1);
        NexRateLimitDecision c = limiter.TryConsume("client-a", t + 2);
        NexRateLimitDecision blocked = limiter.TryConsume("client-a", t + 3);

        Require(a.Accepted && a.Remaining == 2, "first request accounting failed");
        Require(b.Accepted && b.Remaining == 1, "second request accounting failed");
        Require(c.Accepted && c.Remaining == 0, "third request accounting failed");
        Require(!blocked.Accepted, "fourth request should be limited");
        Require(blocked.RetryAfterSeconds == 57, "retry-after calculation mismatch");
        Require(blocked.ResetAt == 180, "reset timestamp mismatch");

        NexRateLimitDecision other = limiter.TryConsume("client-b", t + 3);
        Require(other.Accepted && other.Remaining == 2, "clients must have independent buckets");

        NexRateLimitDecision nextWindow = limiter.TryConsume("client-a", 180);
        Require(nextWindow.Accepted && nextWindow.Remaining == 2, "new window did not reset");

        int removed = limiter.RemoveIdleBuckets(180);
        Require(removed == 1, "idle bucket cleanup did not remove only stale client-b");
        Require(limiter.Limit == 3 && limiter.WindowSeconds == 60, "limiter configuration changed");

        Console.WriteLine("NexVerse API rate limiter regression: OK");
        return 0;
    }
}
