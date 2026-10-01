using System;
using System.Collections.Generic;
using System.IO;
using NexVerse.Core.Audit;

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
            "nexverse-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "audit.jsonl");

        try
        {
            PersistentNexAuditStore store =
                new PersistentNexAuditStore(path);

            NexAuditEvent first = new NexAuditEvent(
                "admin-a",
                "users.update",
                "user-1",
                "corr-1",
                new Dictionary<string, string> { ["field"] = "email" });
            NexAuditEvent second = new NexAuditEvent(
                "admin-b",
                "users.state.update",
                "user-1",
                "corr-2",
                new Dictionary<string, string> { ["state"] = "locked" });
            NexAuditEvent third = new NexAuditEvent(
                "admin-a",
                "auth.client.create",
                "client-1",
                "corr-3");

            store.Record(first);
            store.Record(second);
            store.Record(third);

            NexAuditQueryResult userHistory =
                store.Query("user-1", null, "users.*", 10, 0);

            Require(userHistory.Events.Count == 2, "resource/action query failed");
            Require(userHistory.Events[0].EventId == second.EventId, "newest event must be first");
            Require(!userHistory.HasMore, "unexpected has_more");

            NexAuditQueryResult page =
                store.Query(null, null, null, 2, 0);
            Require(page.Events.Count == 2 && page.HasMore, "pagination failed");

            PersistentNexAuditStore reloaded =
                new PersistentNexAuditStore(path);
            NexAuditQueryResult persisted =
                reloaded.Query(null, "admin-a", null, 10, 0);

            Require(persisted.Events.Count == 2, "audit events did not persist");
            Require(persisted.Events[0].CorrelationId == "corr-3", "correlation ID did not persist");

            Console.WriteLine("NexVerse persistent audit store regression: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
