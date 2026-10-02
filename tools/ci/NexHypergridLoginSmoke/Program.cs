using System;
using System.Collections.Generic;
using System.Net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Connectors.Hypergrid;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

internal static class Program
{
    private static readonly UUID UserId =
        new UUID("6f4d3a90-3b71-4f15-a804-2d94c8d7a201");

    private static readonly UUID DestinationRegionId =
        new UUID("1c6aa9e1-6cef-4c01-a4e4-6b4d19449ef1");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        const string homeUri = "http://127.0.0.1:19200/";
        const string destinationUri = "http://127.0.0.1:19300/";

        UserAgentServiceConnector homeAgent =
            new UserAgentServiceConnector(homeUri);

        GridRegion source =
            new GridRegion
            {
                RegionID = new UUID("781a1daa-a491-4f31-bd82-148124d0e001"),
                RegionName = "NexVerse HG Source",
                RegionLocX = 1200 * 256,
                RegionLocY = 1200 * 256,
                RegionSizeX = 256,
                RegionSizeY = 256,
                ExternalHostName = "127.0.0.1",
                HttpPort = 19200,
                InternalEndPoint = new IPEndPoint(IPAddress.Loopback, 19201),
                ServerURI = homeUri
            };

        GridRegion gatekeeper =
            new GridRegion
            {
                RegionID = DestinationRegionId,
                RegionName = "NexVerse HG Landing",
                RegionLocX = 1300 * 256,
                RegionLocY = 1300 * 256,
                RegionSizeX = 256,
                RegionSizeY = 256,
                ExternalHostName = "127.0.0.1",
                HttpPort = 19300,
                InternalEndPoint = new IPEndPoint(IPAddress.Loopback, 19301),
                ServerURI = destinationUri
            };

        GridRegion destination =
            new GridRegion(gatekeeper);

        AgentCircuitData agent =
            new AgentCircuitData
            {
                AgentID = UserId,
                SessionID = UUID.Random(),
                SecureSessionID = UUID.Random(),
                BaseFolder = UUID.Zero,
                InventoryFolder = UUID.Zero,
                CapsPath = UUID.Random().ToString(),
                circuitcode = 0x4e585647,
                firstname = "NexVerseCI",
                lastname = "Resident",
                IPAddress = "127.0.0.1",
                Viewer = "Firestorm-Release HG CI",
                Channel = "Firestorm-Releasex64",
                Mac = "00:00:00:00:00:02",
                Id0 = "nexverse-ci-hypergrid",
                startpos = new Vector3(128f, 128f, 25f),
                Appearance = new AvatarAppearance(),
                ServiceURLs = new Dictionary<string, object>
                {
                    ["HomeURI"] = homeUri,
                    ["GatekeeperURI"] = homeUri,
                    ["InventoryServerURI"] = homeUri,
                    ["AssetServerURI"] = homeUri,
                    ["FriendsServerURI"] = homeUri
                }
            };

        bool success =
            homeAgent.LoginAgentToGrid(
                source,
                agent,
                gatekeeper,
                destination,
                true,
                out string reason);

        Require(success, "Hypergrid LoginAgentToGrid failed: " + reason);

        Console.WriteLine(
            "NexVerse Hypergrid HomeAgent -> Gatekeeper login: OK session=" +
            agent.SessionID);
        return 0;
    }
}
