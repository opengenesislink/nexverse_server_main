// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using NexVerse.Core.Pathfinding;

static void Require(bool passed, string label)
{
    if (!passed) throw new Exception("FAILED: " + label);
    Console.WriteLine("PASS: " + label);
}
static bool Throws(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
    catch (InvalidOperationException) { return true; }
}
static IReadOnlyList<OglVerifiedSurfaceContact> Floors(float x, float y) =>
    new[] {
        new OglVerifiedSurfaceContact(0u, 0f, 1f),
        new OglVerifiedSurfaceContact(99u, 8f, 1f)
    };

var graph = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f, Floors);
Require(graph.NodeCount == 12, "Ground and verified elevated collision layer sampled");
Require(graph.TryFindWorldPath(1, 1, 0, 5, 1, 0, 0.5f,
    out var groundPath, out int groundCode) && groundCode == 0 &&
    groundPath.Count >= 3 && groundPath[0].Z == 0,
    "Verified terrain route across measured edges");
Require(graph.TryFindWorldPath(1, 1, 8, 5, 1, 8, 0.5f,
    out var bridgePath, out int roofCode) && roofCode == 0 &&
    bridgePath.Count >= 3 && bridgePath[^1].Z == 8,
    "Verified elevated bridge route without inferred vertical transitions");
Require(!graph.TryFindWorldPath(1, 1, 0, 5, 1, 8, 0.5f,
    out _, out int disconnectedCode) && disconnectedCode == 4,
    "No inferred floor-to-bridge portal");

var missingBorder = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => Math.Abs(x - 2f) < 0.01f
        ? Array.Empty<OglVerifiedSurfaceContact>() : Floors(x, y));
Require(!missingBorder.TryFindWorldPath(1, 1, 0, 5, 1, 0, 0.5f,
    out _, out int gapCode) && gapCode == 4,
    "Missing boundary physics support blocks A-star adjacency");

var distinctSource = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => new[] { new OglVerifiedSurfaceContact(
        x < 2f ? 30u : 31u, 0f, 1f) });
Require(!distinctSource.TryFindWorldPath(1, 1, 0, 5, 1, 0, 0.5f,
    out _, out int sourceCode) && sourceCode == 4,
    "Identical heights on different actors are not automatically connected");

Require(Throws(() => OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => null)), "Physics query failure never becomes walkable");
Require(Throws(() => OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => new[] { new OglVerifiedSurfaceContact(0u,
        float.NaN, 1f) })), "Invalid physics elevations rejected");
Require(Throws(() => OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => new[] {
        new OglVerifiedSurfaceContact(1u, 0f, 1f),
        new OglVerifiedSurfaceContact(2u, 2f, 1f),
        new OglVerifiedSurfaceContact(3u, 4f, 1f),
        new OglVerifiedSurfaceContact(4u, 6f, 1f)
    })), "More than three surface levels fail closed");
Require(Throws(() => OglVerifiedSurfaceGraphBuilder.Build(512, 512,
    4, 0.6f, Floors)), "VAR-region ray budget enforced");
Require(Throws(() => new OglLayeredNavGraph(
    new[] {
        new OglLayerNavNode(0, 0, 0, 0, 0.5f),
        new OglLayerNavNode(2, 0, 0, 0, 0.5f)
    },
    Array.Empty<OglLayerNavPortal>(), 2f, 0.6f,
    new[] { new OglLayerNavEdge(0, 1) })),
    "Nonadjacent forged verified edges rejected");
Console.WriteLine("OGL verified collision surface graph regression: OK");
