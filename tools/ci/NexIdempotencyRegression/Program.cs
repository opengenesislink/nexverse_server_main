using System;
using System.IO;
using System.Text;
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
        string root = Path.Combine(
            Path.GetTempPath(),
            "nexverse-idem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "idempotency.json");

        try
        {
            PersistentNexIdempotencyStore store =
                new PersistentNexIdempotencyStore(path);

            NexIdempotencyBeginResult first =
                store.TryBegin("admin|POST|/users", "key-1", "hash-a", 3600);
            Require(first.State == NexIdempotencyBeginState.New, "first reservation must be new");

            NexIdempotencyBeginResult concurrent =
                store.TryBegin("admin|POST|/users", "key-1", "hash-a", 3600);
            Require(concurrent.State == NexIdempotencyBeginState.InProgress, "concurrent duplicate must be in-progress");

            NexIdempotencyBeginResult conflict =
                store.TryBegin("admin|POST|/users", "key-1", "hash-b", 3600);
            Require(conflict.State == NexIdempotencyBeginState.Conflict, "different payload must conflict");

            byte[] body = Encoding.UTF8.GetBytes("{\"ok\":true}");
            store.Complete(
                "admin|POST|/users",
                "key-1",
                "hash-a",
                201,
                "application/json",
                body,
                3600);

            NexIdempotencyBeginResult replay =
                store.TryBegin("admin|POST|/users", "key-1", "hash-a", 3600);
            Require(replay.State == NexIdempotencyBeginState.Replay, "completed request must replay");
            Require(replay.Response.StatusCode == 201, "status did not persist");
            Require(Encoding.UTF8.GetString(replay.Response.Body) == "{\"ok\":true}", "body did not persist");

            PersistentNexIdempotencyStore reloaded =
                new PersistentNexIdempotencyStore(path);
            NexIdempotencyBeginResult replayAfterRestart =
                reloaded.TryBegin("admin|POST|/users", "key-1", "hash-a", 3600);
            Require(replayAfterRestart.State == NexIdempotencyBeginState.Replay, "replay did not survive restart");

            NexIdempotencyBeginResult pending =
                reloaded.TryBegin("admin|POST|/users", "key-2", "hash-c", 3600);
            Require(pending.State == NexIdempotencyBeginState.New, "second key reservation failed");
            reloaded.Abort("admin|POST|/users", "key-2", "hash-c");
            NexIdempotencyBeginResult retry =
                reloaded.TryBegin("admin|POST|/users", "key-2", "hash-c", 3600);
            Require(retry.State == NexIdempotencyBeginState.New, "aborted reservation was not released");

            Console.WriteLine("NexVerse idempotency store regression: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
