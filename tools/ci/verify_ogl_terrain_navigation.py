#!/usr/bin/env python3
"""Protect experimental per-region terrain navigation integration."""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
core = (root/"NexVerse/Core/Pathfinding/OglTerrainNavigationSnapshot.cs").read_text()
module = (root/"NexVerse/RegionModules/Pathfinding/OglTerrainNavigationModule.cs").read_text()
iface = (root/"NexVerse/RegionModules/Pathfinding/IOglTerrainNavigationRegion.cs").read_text()
config = (root/"bin/OpenSim.ini.example").read_text()
for word in ("OglGridPathfinder", "maxSlopePerMeter", "waterHeight",
             "staticObstacleAt", "TryFindWorldPath", "float.IsFinite",
             "regionWidth", "GridCell"):
    assert word in core, word
for word in ('Configs["OGLPathfinding"]', "Enabled", "OnTerrainTainted",
             "MakeCopy()", "ITerrainChannel", "Volatile.Write",
             "IsNavigationDirty", "Interlocked.Exchange", "Task.Run",
             "RegisterModuleInterface<IOglTerrainNavigationRegion>",
             "UnregisterModuleInterface<IOglTerrainNavigationRegion>"):
    assert word in module, word
for safety_marker in (
    "internal int Epoch = 1;",
    "internal int SnapshotEpoch;",
    "Interlocked.Increment(ref Epoch)",
    "Volatile.Read(ref state.Epoch) == buildEpoch",
    "requestedEpoch != Volatile.Read(ref Epoch)",
    "Volatile.Read(ref Building) != 0",
):
    assert safety_marker in module, f"terrain rebuild epoch safety lost: {safety_marker}"
assert "Interlocked.Exchange(ref state.Dirty, 0);" not in module, "stale navigation was exposed before rebuild"
assert "TryFindNearestTerrainPoint" in iface
assert "TryFindNearestTerrainPoint" in module
assert "TryFindNearestTerrainPoint" in core
lsl = Path("OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs").read_text(encoding="utf-8")
lsl_interface = Path("OpenSim/Region/ScriptEngine/Shared/Api/Interface/ILSL_Api.cs").read_text(encoding="utf-8")
lsl_stub = Path("OpenSim/Region/ScriptEngine/Shared/Api/Runtime/LSL_Stub.cs").read_text(encoding="utf-8")
native_iface = Path("OpenSim/Region/Framework/Interfaces/IOglNativeTerrainQuery.cs").read_text(encoding="utf-8")
for marker in ("llGetClosestNavPoint", "GCNP_RADIUS", "GCNP_STATIC", "CHARACTER_TYPE_NONE"):
    assert marker in lsl, f"missing LSL nearest navigation handler: {marker}"
assert "llGetClosestNavPoint" in lsl_stub and "llGetClosestNavPoint" in lsl_interface
assert "TryGetClosestNavPoint" in native_iface
assert "TryGetStaticTerrainPath" in native_iface
assert "TryGetStaticTerrainPath" in module
assert "TryFindStaticTerrainRoute" in core
layered = (root/"NexVerse/Core/Pathfinding/OglLayeredNavGraph.cs").read_text(encoding="utf-8")
for marker in (
    "class OglLayeredNavGraph",
    "readonly struct OglLayerNavNode",
    "readonly struct OglLayerNavPortal",
    "MaxNodes = 65536",
    "MaxPortals = 32768",
    "MaxPathNodes = 512",
    "TryFindPath(int start, int target, float agentRadius",
    "m_Grid.TryAdd",
    "MaxAgentRadius >= radius",
    "if (dx != 0 && dy != 0)",
    "foreach (int next in m_Portals[current])",
):
    assert marker in layered, f"3D multi-layer graph safety contract missing: {marker}"
layer_regressions = (root/"tools/ci/OglTerrainNavigationRegression/Program.cs").read_text()
for marker in (
    "overlapping floor and bridge have no implicit vertical shortcut",
    "verified stairs portal joins ground to elevated bridge",
    "off-mesh portals enforce direction",
    "multi-level A* CPU expansion budget enforced",
    "multi-level diagonal corner cutting blocked",
    "duplicate multi-level XY-layer cell rejected",
):
    assert marker in layer_regressions, f"3D multi-layer test missing: {marker}"

for guard in ("class OglTerrainStaticObstacles", "OglStaticCollisionAabb",
              "Too many static obstacle-cell intersections"):
    assert guard in core, f"static prim collider guard missing: {guard}"
for guard in ("TrackStaticColliders", "CaptureStaticColliders",
              "OnObjectAddedToScene", "OnObjectBeingRemovedFromScene",
              "OnSceneObjectPartUpdated", "PhysicsShapeType.None",
              "OglTerrainStaticObstacles.Project"):
    assert guard in module, f"static Scene collision integration missing: {guard}"
assert "TrackStaticColliders = false" in config
assert "static prim collision across a region must block" in (root/"tools/ci/OglTerrainNavigationRegression/Program.cs").read_text()
grid = (root/"NexVerse/Core/Pathfinding/OglGridPathfinder.cs").read_text()
terrain_regression = (root/"tools/ci/OglTerrainNavigationRegression/Program.cs").read_text()
assert "Func<GridCell, bool> additionalWalkability" in grid
assert "CanWalk(new GridCell(x + dx, y))" in grid
assert "CellHasTerrainClearance" in core
assert "out IReadOnlyList<GridCell> route, maxExpanded, HasClearance" in core
assert "large avatar clearance rejects corridor touching obstacles" in terrain_regression
assert "TryFindStaticTerrainRoute" in module
assert "PU_FAILURE_NO_NAVMESH" in module
assert "llGetStaticPath" in lsl_interface and "llGetStaticPath" in lsl_stub
for marker in (
    "public LSL_List llGetStaticPath(",
    "ScriptBaseClass.PU_FAILURE_NO_NAVMESH",
    "ScriptBaseClass.PU_FAILURE_OTHER",
    "ScriptBaseClass.CHARACTER_TYPE_NONE",
    "new LSL_List(elements)"
):
    assert marker in lsl, f"missing native static path LSL contract: {marker}"
constants = Path("OpenSim/Region/ScriptEngine/Shared/Api/Runtime/LSL_Constants.cs").read_text(encoding="utf-8")
for marker in ("PU_FAILURE_NO_NAVMESH = 9", "PU_FAILURE_UNREACHABLE = 4", "PU_FAILURE_OTHER = 0xF4240"):
    assert marker in constants, f"missing SL static path failure constant: {marker}"
assert "RegisterModuleInterface<IOglNativeTerrainQuery>" in module
assert "UnregisterModuleInterface<IOglNativeTerrainQuery>" in module
assert "TryFindTerrainPath" in iface
assert "RetrieveNavMeshSrc intentionally not advertised" in module
assert 'RegisterSimpleHandler("RetrieveNavMeshSrc"' not in module
assert "Enabled = false" in config and "[OGLPathfinding]" in config
assert "llNavigateTo" not in module and "new ScenePresence" not in module
print("OGL per-region terrain navigation module wiring: OK")
