using System;
using System.Collections.Generic;
using System.Linq;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Messaging;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static NexEvent Event(
        string name,
        Dictionary<string, string> data)
    {
        return new NexEvent(
            name,
            "nexverse.simulator",
            data,
            "node-regression");
    }

    private static int Main()
    {
        InMemoryNexEventBus bus = new InMemoryNexEventBus();

        using NexNodeRegistry registry =
            new NexNodeRegistry(bus, 90);

        bus.Publish(Event(
            "node.online",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["hostname"] = "sim-a.internal",
                ["server_version"] = "NexVerse 0.9.3.3 Dev",
                ["uptime_seconds"] = "120",
                ["process_id"] = "4242",
                ["working_set_bytes"] = "1048576",
                ["cpu_seconds"] = "3.500",
                ["region_count"] = "2",
                ["agent_count"] = "7",
                ["regions"] =
                    "11111111-1111-1111-1111-111111111111|Alpha;" +
                    "22222222-2222-2222-2222-222222222222|Beta"
            }));

        NexNodeSnapshot first =
            registry.Get("sim-a");

        Require(first != null, "node.online did not create registry entry");
        Require(first.State == "online", "new node must be online");
        Require(first.Hostname == "sim-a.internal", "hostname mismatch");
        Require(first.RegionCount == 2, "heartbeat region count mismatch");
        Require(first.AgentCount == 7, "heartbeat agent count mismatch");
        Require(first.Regions.Count == 2, "heartbeat region list mismatch");

        bus.Publish(Event(
            "region.online",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["region_id"] = "11111111-1111-1111-1111-111111111111",
                ["region_name"] = "Alpha",
                ["server_uri"] = "http://sim-a.internal:9000/",
                ["size_x"] = "512",
                ["size_y"] = "256",
                ["agent_count"] = "4"
            }));

        NexNodeSnapshot owner =
            registry.FindNodeForRegion(
                "11111111-1111-1111-1111-111111111111");

        Require(owner != null && owner.NodeId == "sim-a",
            "region ownership lookup failed");

        NexNodeRegionSnapshot alpha =
            owner.Regions.First(x =>
                x.RegionId ==
                "11111111-1111-1111-1111-111111111111");

        Require(alpha.SizeX == 512 && alpha.SizeY == 256,
            "region.online size metadata mismatch");
        Require(alpha.AgentCount == 4,
            "region.online agent metadata mismatch");

        bus.Publish(Event(
            "node.heartbeat",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["region_count"] = "1",
                ["agent_count"] = "4",
                ["regions"] =
                    "11111111-1111-1111-1111-111111111111|Alpha"
            }));

        NexNodeSnapshot pruned =
            registry.Get("sim-a");

        Require(pruned.Regions.Count == 1,
            "heartbeat must prune regions no longer reported");
        Require(registry.FindNodeForRegion(
                    "22222222-2222-2222-2222-222222222222") == null,
            "removed heartbeat region must not retain node ownership");

        bus.Publish(Event(
            "node.offline",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["regions"] =
                    "11111111-1111-1111-1111-111111111111|Alpha"
            }));

        Require(registry.Get("sim-a").State == "offline",
            "node.offline must mark node offline");

        bus.Publish(Event(
            "node.heartbeat",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["region_count"] = "1",
                ["agent_count"] = "4",
                ["regions"] =
                    "11111111-1111-1111-1111-111111111111|Alpha"
            }));

        Require(registry.Get("sim-a").State == "online",
            "heartbeat must revive an offline node");

        bus.Publish(Event(
            "region.offline",
            new Dictionary<string, string>
            {
                ["node_id"] = "sim-a",
                ["region_id"] = "11111111-1111-1111-1111-111111111111"
            }));

        NexNodeSnapshot empty =
            registry.Get("sim-a");

        Require(empty.RegionCount == 0 && empty.Regions.Count == 0,
            "region.offline must remove region from node");
        Require(registry.FindNodeForRegion(
                    "11111111-1111-1111-1111-111111111111") == null,
            "offline region must no longer resolve to a node");

        Require(registry.StaleAfterSeconds == 90,
            "registry stale threshold mismatch");

        Console.WriteLine("NexVerse NodeAgent registry regression: OK");
        return 0;
    }
}
