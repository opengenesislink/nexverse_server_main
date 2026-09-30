using System;
using System.Collections;
using System.Net;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        IniConfigSource config = new IniConfigSource();
        IConfig xmlrpc = config.AddConfig("XMLRPC");
        xmlrpc.Set("XmlRpcPort", 20800);

        XMLRPCModule module = new XMLRPCModule();
        module.Initialise(config);

        Require(module.IsEnabled(), "XMLRPCModule did not enable with a configured port");
        Require(module.Port == 20800, "XMLRPCModule port mismatch");

        UUID itemA = UUID.Random();
        UUID channelA = module.OpenXMLRPCChannel(41, itemA, UUID.Zero);
        Require(!channelA.IsZero(), "llOpenRemoteDataChannel-compatible channel creation failed");

        UUID duplicate = module.OpenXMLRPCChannel(41, itemA, UUID.Zero);
        Require(duplicate == channelA, "opening a second channel for the same script item changed the channel");

        UUID itemB = UUID.Random();
        UUID requestedChannel = UUID.Random();
        UUID channelB = module.OpenXMLRPCChannel(42, itemB, requestedChannel);
        Require(channelB == requestedChannel, "custom RemoteData channel was not preserved");

        module.CloseXMLRPCChannel(channelA);

        Hashtable requestData = new Hashtable
        {
            ["Channel"] = channelA.ToString(),
            ["IntValue"] = 7,
            ["StringValue"] = "nexverse-ci"
        };
        ArrayList parameters = new ArrayList { requestData };
        XmlRpcRequest request = new XmlRpcRequest("llRemoteData", parameters);

        XmlRpcResponse response = module.XmlRpcRemoteData(
            request,
            new IPEndPoint(IPAddress.Loopback, 20800));

        Require(response.IsFault, "closed/invalid RemoteData channel did not return an XML-RPC fault");

        module.CloseXMLRPCChannel(channelB);
        Require(!module.hasRequests(), "unexpected pending RemoteData requests remain");

        Console.WriteLine("NexVerse LSL XML-RPC RemoteData runtime regression: OK");
        return 0;
    }
}
