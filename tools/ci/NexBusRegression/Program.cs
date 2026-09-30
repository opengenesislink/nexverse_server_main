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

        byte[] wire = NexBusProtocol.Serialize(ev);
        string sharedKey = new string('N', 64);
        string signature = NexBusProtocol.Sign(wire, sharedKey);
        Require(NexBusProtocol.Verify(wire, sharedKey, "sha256=" + signature), "wire HMAC verification failed");
        Require(!NexBusProtocol.Verify(wire, new string('X', 64), "sha256=" + signature), "wrong NexBus HMAC key accepted");

        NexEvent wireEvent = NexBusProtocol.Deserialize(wire);
        Require(wireEvent.EventId == ev.EventId, "wire EventId mismatch");
        Require(wireEvent.Name == ev.Name, "wire event name mismatch");
        Require(wireEvent.Data["principal_id"] == "demo", "wire event data mismatch");

        Console.WriteLine("NexBus distributed core/wire regression: OK");
        return 0;
    }
}
