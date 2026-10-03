using System;
using System.Collections.Generic;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Messaging;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        InMemoryNexEventBus bus =
            new InMemoryNexEventBus();

        using NexNodeCommandTracker tracker =
            new NexNodeCommandTracker(
                bus,
                15,
                128);

        int requestCount = 0;

        using IDisposable responder =
            bus.Subscribe(
                NexNodeCommandProtocol.RequestEvent,
                request =>
                {
                    requestCount++;

                    Require(
                        request.Data["action"] ==
                        NexNodeCommandProtocol.PingAction,
                        "unexpected command action");
                    Require(
                        Guid.TryParse(
                            request.Data["command_id"],
                            out _),
                        "command ID is not a UUID");
                    Require(
                        long.TryParse(
                            request.Data["expires_at_unix"],
                            out long expiresAt) &&
                        expiresAt >
                        DateTimeOffset.UtcNow
                            .ToUnixTimeSeconds(),
                        "command deadline missing or expired");

                    string target =
                        request.Data["target_node_id"];

                    if (target == "sim-a")
                    {
                        bus.Publish(
                            new NexEvent(
                                NexNodeCommandProtocol.ResultEvent,
                                "nexverse.simulator",
                                new Dictionary<string, string>
                                {
                                    ["command_id"] =
                                        request.Data["command_id"],
                                    ["node_id"] =
                                        target,
                                    ["action"] =
                                        NexNodeCommandProtocol.PingAction,
                                    ["state"] =
                                        NexNodeCommandProtocol.CompletedState,
                                    ["message"] =
                                        "pong"
                                },
                                request.CorrelationId));
                    }
                    else if (target == "sim-b")
                    {
                        bus.Publish(
                            new NexEvent(
                                NexNodeCommandProtocol.ResultEvent,
                                "nexverse.simulator",
                                new Dictionary<string, string>
                                {
                                    ["command_id"] =
                                        request.Data["command_id"],
                                    ["node_id"] =
                                        "wrong-node",
                                    ["action"] =
                                        NexNodeCommandProtocol.PingAction,
                                    ["state"] =
                                        NexNodeCommandProtocol.CompletedState,
                                    ["message"] =
                                        "must-be-ignored"
                                },
                                request.CorrelationId));
                    }
                    else if (target == "sim-c")
                    {
                        bus.Publish(
                            new NexEvent(
                                NexNodeCommandProtocol.ResultEvent,
                                "nexverse.simulator",
                                new Dictionary<string, string>
                                {
                                    ["command_id"] =
                                        request.Data["command_id"],
                                    ["node_id"] =
                                        target,
                                    ["action"] =
                                        NexNodeCommandProtocol.PingAction,
                                    ["state"] =
                                        NexNodeCommandProtocol.RejectedState,
                                    ["message"] =
                                        "policy-rejected"
                                },
                                request.CorrelationId));
                    }
                });

        NexNodeCommandSnapshot completed =
            tracker.IssuePing(
                "sim-a",
                "admin:test",
                "corr-a");

        Require(
            completed.State ==
            NexNodeCommandProtocol.CompletedState,
            "synchronous ping result was not applied");
        Require(
            completed.Message == "pong",
            "ping result message mismatch");
        Require(
            completed.CorrelationId == "corr-a",
            "command correlation ID mismatch");

        NexNodeCommandSnapshot pending =
            tracker.IssuePing(
                "sim-b",
                "admin:test",
                "corr-b");

        Require(
            pending.State ==
            NexNodeCommandProtocol.PendingState,
            "mismatched node result must be ignored");

        NexNodeCommandSnapshot rejected =
            tracker.IssuePing(
                "sim-c",
                "admin:test",
                "corr-c");

        Require(
            rejected.State ==
            NexNodeCommandProtocol.RejectedState,
            "rejected result state mismatch");
        Require(
            rejected.Message == "policy-rejected",
            "rejected result message mismatch");

        NexNodeCommandSnapshot lookup =
            tracker.Get(
                completed.CommandId.ToString());

        Require(
            lookup != null &&
            lookup.State ==
            NexNodeCommandProtocol.CompletedState,
            "command lookup failed");
        Require(
            tracker.List().Count == 3,
            "command history count mismatch");
        Require(
            requestCount == 3,
            "unexpected command request delivery count");

        Console.WriteLine(
            "NexVerse NodeAgent command-plane regression: OK");
        return 0;
    }
}
