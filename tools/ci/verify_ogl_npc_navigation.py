#!/usr/bin/env python3
"""Bounded NPC navigation must use native NPC physics and enforce ownership."""
from pathlib import Path
r = Path(__file__).resolve().parents[2]
module = (r/"NexVerse/RegionModules/Pathfinding/OglNpcPathFollowerModule.cs").read_text()
cursor = (r/"NexVerse/Core/Pathfinding/OglNpcWaypointCursor.cs").read_text()
contract = (r/"NexVerse/RegionModules/Pathfinding/IOglNpcRouteService.cs").read_text()
config = (r/"bin/OpenSim.ini.example").read_text()
for item in ("INPCModule", "CheckPermissions(npcId, callerId)",
             "IsNPC", "MoveToTarget(", "StopMoveToTarget(",
             "IOglTerrainNavigationRegion", "IsNavigationReady",
             "m_MaxActive", "m_MaxWaypoints", "m_WaypointTimeout",
             "m_Timer", "Interlocked.Exchange(ref m_Ticking",
             "UnregisterModuleInterface<IOglNpcRouteService>"):
    assert item in module, item
assert module.count("callerId.IsZero()") >= 2, "reject NPC superuser bypass"
nav_interface = (r/"NexVerse/RegionModules/Pathfinding/IOglTerrainNavigationRegion.cs").read_text()
nav_impl = (r/"NexVerse/RegionModules/Pathfinding/OglTerrainNavigationModule.cs").read_text()
assert "TryFindNpcPath(" in nav_interface and "public bool TryFindNpcPath(" in nav_impl
assert "TryGetStaticTerrainPath(" in nav_impl and "Math.Abs(startZ - waypoints[0].Z)" in nav_impl
assert "Math.Abs(targetZ - waypoints[waypoints.Length - 1].Z)" in nav_impl
assert "TryFindNpcPath(sp.AbsolutePosition.X" in module
assert "TryFindTerrainPath(sp.AbsolutePosition.X" not in module
assert module.count("UpdatePosition3D(") == 2
assert "Math.Abs(point.Z + 1.0f - z)" in cursor
regression = (r/"tools/ci/OglNpcNavigationRegression/Program.cs").read_text()
assert "ground-level arrival must not consume bridge node" in regression
assert "lower level cannot complete upper-level goal" in regression
assert "TryNavigate(" in contract
assert "Stalled(" in cursor and "maximumPoints" in cursor
assert "[OGLNpcNavigation]" in config and "Enabled = false" in config
assert "sp.AbsolutePosition =" not in module
assert "llNavigateTo" not in module
print("OGL owner-checked native NPC waypoint motor integration: OK")
