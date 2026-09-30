using System;
using System.Collections.Generic;
using NexVerse.Core.Messaging;

internal sealed class LoopTransport : INexEventTransport
{
    public Action<NexEvent> Receiver { get; set; }
    public int SendCount { get; private set; }

    public void Send(NexEvent nexEvent)
    {
        SendCount++;
        Receiver?.Invoke(nexEvent);
    }

    public void Dispose()
    {
    }
}

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        LoopTransport aTransport = new LoopTransport();
        LoopTransport bTransport = new LoopTransport();

        using DistributedNexEventBus a = new DistributedNexEventBus(aTransport, 256);
        using DistributedNexEventBus b = new DistributedNexEventBus(bTransport, 256);

        aTransport.Receiver = e => b.Receive(e);
        bTransport.Receiver = e => a.Receive(e);

        int aReceived = 0;
        int bReceived = 0;

        using IDisposable sa = a.Subscribe("user.updated", _ => aReceived++);
        using IDisposable sb = b.Subscribe("user.updated", _ => bReceived++);

        NexEvent ev = new NexEvent(
            "user.updated",
            "regression-node-a",
            new Dictionary<string, string> { ["principal_id"] = "demo" },
            "corr-1");

        a.Publish(ev);

        Require(aReceived == 1, "source node must deliver locally once");
        Require(bReceived == 1, "remote node must deliver once");
        Require(aTransport.SendCount == 1, "source should relay once");
        Require(bTransport.SendCount == 1, "remote node should attempt one relay");

        Require(!b.Receive(ev), "duplicate receive must be rejected");
        Require(aReceived == 1 && bReceived == 1, "duplicates must not redeliver");

        NexEvent reconstructed = new NexEvent(
            ev.EventId,
            ev.Timestamp,
            ev.Name,
            ev.Source,
            ev.Data,
            ev.CorrelationId);

        Require(reconstructed.EventId == ev.EventId, "transport EventId must survive reconstruction");
        Require(reconstructed.Timestamp == ev.Timestamp, "transport timestamp must survive reconstruction");

        Console.WriteLine("NexBus distributed core regression: OK");
        return 0;
    }
}
