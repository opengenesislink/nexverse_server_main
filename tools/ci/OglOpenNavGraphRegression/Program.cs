// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using NexVerse.Core.Pathfinding;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}
static bool Reject(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

var nodes = new[] {
    new OglLayerNavNode(0,0,0,0,0.5f),
    new OglLayerNavNode(1,0,0,0,0.5f),
    new OglLayerNavNode(1,1,1,0.4f,0.5f),
};
var graph = new OglLayeredNavGraph(
    nodes, new[] { new OglLayerNavPortal(1,2) }, 2f, 0.6f,
    new[] { new OglLayerNavEdge(0,1) });
byte[] payload = OglOpenNavGraphCodec.Encode(graph, 19);
Check(OglOpenNavGraphCodec.TryDecode(payload,
    out int epoch, out float cell, out var decodedNodes,
    out var decodedArcs) && epoch == 19 && cell == 2f &&
    decodedNodes.Length == 3 && decodedArcs.Length == 4 &&
    decodedArcs.Count(a=>a.OffMesh) == 2 &&
    decodedArcs.Count(a=>!a.OffMesh) == 2,
    "Deterministic certified planar edges and bilateral stair portal");
Check(payload.SequenceEqual(OglOpenNavGraphCodec.Encode(graph, 19)),
    "Byte identical snapshot serialization");

var reordered = new OglLayeredNavGraph(
    nodes, new[] { new OglLayerNavPortal(1,2) }, 2f, 0.6f,
    new[] { new OglLayerNavEdge(1,0) });
Check(payload.SequenceEqual(OglOpenNavGraphCodec.Encode(reordered,19)),
    "Verified edge insertion order never changes encoded payload");
Check(!payload.SequenceEqual(OglOpenNavGraphCodec.Encode(graph,20)),
    "Snapshot epoch changes transport identity");
byte[] tampered=(byte[])payload.Clone();
tampered[30]^=1;
Check(!OglOpenNavGraphCodec.TryDecode(tampered,out _,out _,out _,out _),
    "Checksum catches mutated floor heights");
Check(!OglOpenNavGraphCodec.TryDecode(payload[..^1],
    out _,out _,out _,out _), "Truncated payload fails closed");
Check(!OglOpenNavGraphCodec.TryDecode(new byte[100],
    out _,out _,out _,out _), "Unsigned fabricated mesh fails closed");
Check(Reject(()=> OglOpenNavGraphCodec.Encode(
    new OglLayeredNavGraph(nodes,
        new[] {new OglLayerNavPortal(1,2)},2f,0.6f),19)),
    "Legacy implicit-grid A-star cannot masquerade as certified topology");
Check(Reject(()=>OglOpenNavGraphCodec.Encode(graph,0)),
    "Invalid nonpositive generation cannot be published");

var directed = new OglLayeredNavGraph(nodes,
    new[]{new OglLayerNavPortal(1,2,false)},2f,0.6f,
    new []{new OglLayerNavEdge(0,1)});
byte[] oneWay=OglOpenNavGraphCodec.Encode(directed,19);
Check(OglOpenNavGraphCodec.TryDecode(oneWay,
    out _,out _,out _,out var arcs1) &&
    arcs1.Count(a=>a.OffMesh) == 1 && arcs1.Length == 3,
    "Unidirectional lift portal retains directionality");
Console.WriteLine("OGLNAVGRAPH/1 transport regression: OK");
