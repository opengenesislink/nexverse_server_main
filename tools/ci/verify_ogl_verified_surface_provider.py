#!/usr/bin/env python3
"""Reject false scene floors, unsupported physics and implicit transitions."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
module = (root / "NexVerse/RegionModules/Pathfinding/OglVerifiedRaycastSurfacesModule.cs").read_text()
builder = (root / "NexVerse/Core/Pathfinding/OglVerifiedSurfaceGraphBuilder.cs").read_text()
graph = (root / "NexVerse/Core/Pathfinding/OglLayeredNavGraph.cs").read_text()
config = (root / "bin/OpenSim.ini.example").read_text()
prebuild = (root / "prebuild.xml").read_text()
tests = (root / "tools/ci/OglVerifiedCollisionSurfaceRegression/Program.cs").read_text()
for mark in (
    'config.GetBoolean("PhysicsRaycastLayeredSurfaces", false)',
    'config.GetBoolean("UseVerifiedLayeredSurfaces", false)',
    "IOglVerifiedLayeredSurfaceSource",
    "scene.SupportsRayCastFiltered()",
    "RayFilterFlags.land | RayFilterFlags.nonphysical",
    "scene.RayCastFiltered(",
    "scene.GetSceneObjectPart(hit.ConsumerID)",
    "part.PhysicsShapeType",
    "group.IsPhantom",
    "group.UsesPhysics",
    "terrain[(int)x, (int)y]",
    "52f, 16",
    "PhysicsRaycastMaxBuildSeconds",
    "m_MaxSeconds",
    "m_Regions.TryGetValue",
    "OglVerifiedSurfaceGraphBuilder.Build(",
    "UnregisterModuleInterface<IOglVerifiedLayeredSurfaceSource>",
):
    assert mark in module, f"unverified scene/physics source: {mark}"
for mark in (
    "MaxCells = 4096",
    "MaxHitsPerRay = 16",
    "MaxLayers = 3",
    "IReadOnlyList<OglVerifiedSurfaceContact>",
    "sample(x, y)",
    "Ray(cx + sx * patch, cy + sy * patch)",
    "HasContact(Ray(borderX, borderY)",
    "from.SourceId != to.SourceId",
    "Array.Empty<OglLayerNavPortal>()",
    "new OglLayerNavEdge(from.Index, to.Index)",
):
    assert mark in builder, f"unsafe verified graph generation: {mark}"
for mark in (
    "readonly struct OglLayerNavEdge",
    "m_VerifiedPlanarEdges",
    "m_VerifiedPlanarEdges.Contains(PairKey(a, b))",
    "dx + dy != 1",
):
    assert mark in graph, f"verified planar edge guard missing: {mark}"
for mark in (
    "No inferred floor-to-bridge portal",
    "Missing boundary physics support blocks A-star adjacency",
    "Identical heights on different actors",
    "Nonadjacent forged verified edges rejected",
):
    assert mark in tests, f"missing collision graph regression: {mark}"
assert "PhysicsRaycastLayeredSurfaces = false" in config
assert "FirestormNavMeshCaps = false" in config
assert '<Reference name="OpenSim.Region.PhysicsModules.SharedBase"/>' in prebuild
print("OGL verified physics collision surface provider guards: OK")
