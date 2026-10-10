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
assert "TryFindNearestTerrainPoint" in snapshot
assert "TryFindTerrainPath" in iface
assert "RetrieveNavMeshSrc intentionally not advertised" in module
assert 'RegisterSimpleHandler("RetrieveNavMeshSrc"' not in module
assert "Enabled = false" in config and "[OGLPathfinding]" in config
assert "llNavigateTo" not in module and "new ScenePresence" not in module
print("OGL per-region terrain navigation module wiring: OK")
