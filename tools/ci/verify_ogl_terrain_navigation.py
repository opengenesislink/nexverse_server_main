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
assert "TryFindTerrainPath" in iface
assert "RetrieveNavMeshSrc intentionally not advertised" in module
assert 'RegisterSimpleHandler("RetrieveNavMeshSrc"' not in module
assert "Enabled = false" in config and "[OGLPathfinding]" in config
assert "llNavigateTo" not in module and "new ScenePresence" not in module
print("OGL per-region terrain navigation module wiring: OK")
