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
assert "TryNavigate(" in contract
assert "Stalled(" in cursor and "maximumPoints" in cursor
assert "[OGLNpcNavigation]" in config and "Enabled = false" in config
assert "sp.AbsolutePosition =" not in module
assert "llNavigateTo" not in module
print("OGL owner-checked native NPC waypoint motor integration: OK")
