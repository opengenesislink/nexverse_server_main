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

// Real static stair treads on two different actors are connected only
// after BOTH border contact checks and a certified free-space corridor.
var stairs = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => new [] {
        new OglVerifiedSurfaceContact(x < 2f ? 10u : 11u,
            x < 2f ? 0f : 0.4f, 1f)
    },
    clearCorridor: (a, b) => true, verifiedTransitions: true);
Require(stairs.TryFindWorldPath(1, 1, 0, 5, 1, 0.4f, 0.5f,
    out var treadPath, out int treadStatus) && treadStatus == 0 &&
    treadPath.Count >= 3,
    "Measured adjoining stairs transition between different static actors");

var gapBetweenTreads = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => x >= 1.9f && x < 2.1f
        ? Array.Empty<OglVerifiedSurfaceContact>() :
          new[] { new OglVerifiedSurfaceContact(x < 2f ? 10u : 11u,
              x < 2f ? 0f : 0.4f, 1f) },
    clearCorridor: (a, b) => true, verifiedTransitions: true);
Require(!gapBetweenTreads.TryFindWorldPath(1, 1, 0, 5, 1, 0.4f, 0.5f,
    out _, out int treadGap) && treadGap == 4,
    "Detached stair treads are not connected across empty boundary");

// When the lower floor disappears, its former upper surface changes
// per-cell layer ordinal; a measured physical ramp may connect these.
var layerShift = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => x < 2f
        ? new[] { new OglVerifiedSurfaceContact(0, 0, 1),
                  new OglVerifiedSurfaceContact(99, 4, 1) }
        : new[] { new OglVerifiedSurfaceContact(99, 4, 1) },
    clearCorridor: (a, b) => true, verifiedTransitions: true);
Require(layerShift.TryFindWorldPath(1, 1, 4, 5, 1, 4, 0.5f,
    out var highRoute, out int highStatus) && highStatus == 0 &&
    highRoute.Count >= 3,
    "Measured same physical bridge remains connected across layer ordinal shift");
Require(!layerShift.TryFindWorldPath(1, 1, 0, 5, 1, 4, 0.5f,
    out _, out int impossiblePortal) && impossiblePortal == 4,
    "Disconnected ground never inherits bridge portal");

var deniedEdge = OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    (x, y) => new[] { new OglVerifiedSurfaceContact(9, 0, 1) },
    clearCorridor: (a, b) =>
        Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f,
    verifiedTransitions: true);
Require(!deniedEdge.TryFindWorldPath(1, 1, 0, 5, 1, 0, 0.5f,
    out _, out int wallStatus) && wallStatus == 4,
    "Impassable wall corridor prevents otherwise walkable path");

Require(Throws(() => OglVerifiedSurfaceGraphBuilder.Build(6, 4, 2, 0.6f,
    Floors, verifiedTransitions: true)),
    "Stairs must not be enabled without a clearance verifier");

int freeRayCount = 0;
Require(OglMultiRayAgentClearance.IsCorridorClear(
    new OglClearancePoint(1, 1, 0), new OglClearancePoint(3, 1, 0),
    0.5f, 1.8f, (start, end) => { ++freeRayCount; return true; }) &&
    freeRayCount == 19,
    "Multi-ray corridor probes both standing volumes and nine travel lines");
Require(!OglMultiRayAgentClearance.IsCorridorClear(
    new OglClearancePoint(1, 1, 0), new OglClearancePoint(3, 1, 0),
    0.5f, 1.8f, (start, end) =>
        !(end.X > start.X && start.Z > 0.5f && start.Z < 1.5f)),
    "Wall crossing a middle-body collision ray blocks route");
Require(!OglMultiRayAgentClearance.IsCorridorClear(
    new OglClearancePoint(1, 1, 0), new OglClearancePoint(1, 1, 0),
    0.5f, 1.8f, (start, end) => false),
    "Overhead obstruction blocks the avatar standing volume");
Require(!OglMultiRayAgentClearance.IsCorridorClear(
    new OglClearancePoint(1, 1, 0), new OglClearancePoint(3, 1, 0),
    float.NaN, 1.8f, (start, end) => true),
    "Invalid character radius cannot bypass clearance checking");
Require(Throws(() => new OglLayeredNavGraph(
    new[] { new OglLayerNavNode(0, 0, 0, 0, 0.5f),
            new OglLayerNavNode(3, 0, 1, 0.3f, 0.5f) },
    new[] { new OglLayerNavPortal(0, 1) }, 2f, 0.6f,
    Array.Empty<OglLayerNavEdge>())),
    "Strict physics graph rejects remote fabricated stair portal");

Console.WriteLine("OGL verified collision surface graph regression: OK");
