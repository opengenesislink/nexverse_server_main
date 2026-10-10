#!/usr/bin/env python3
"""Firestorm viewer NavMesh CAPS only after verified native scene publication."""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
caps=(ROOT/"NexVerse/RegionModules/Pathfinding/OglFirestormNavMeshCapsModule.cs").read_text()
wire=(ROOT/"NexVerse/Core/Pathfinding/OglFirestormNavMeshTransport.cs").read_text()
config=(ROOT/"bin/OpenSim.ini.example").read_text()
for required in (
    'FirestormNavMeshCaps", false',
    "IOglFirestormNavMeshSource",
    "TryCaptureFirestormSnapshot",
    "IOglTerrainNavigationRegion",
    "!navigation.IsNavigationReady",
    "navigation.IsNavigationDirty",
    "if (!TryCapture(state, out _)) return;",
    '"RetrieveNavMeshSrc"',
    '"NavMeshGenerationStatus"',
    '"RegionObjects"',
    '"CharacterProperties"',
    '"TerrainNavMeshProperties"',
    '"AgentState"',
    '"can_modify_navmesh"',
    'OSD.FromBoolean(false)',
    '"navmesh_version"',
    '"navmesh_data"',
    'OSD.FromBinary(',
    'OSD.FromString("complete")',
    '"POST" : "GET"',
    'TryGetScenePresence(avatar',
    'agent.IsChildAgent',
    'agent.IsNPC',
    'ServiceUnavailable',
    'OnRegisterCaps -= state.CapsListener',
):
    assert required in caps, f"missing Firestorm CAPS fail-closed guard: {required}"
for forbidden in (
    'RegisterReadOnlyCap("ObjectNavMeshProperties"',
    'FirestormNavMeshCaps", true',
    "DummyNavMesh",
):
    assert forbidden not in caps, f"unsafe cap publishing: {forbidden}"
for required in (
    "MaxCompressedBytes = 8 * 1024 * 1024",
    "MaxExpandedBytes = 32 * 1024 * 1024",
    "GZipStream",
    "ZLibStream",
    "SHA256.HashData",
    "m_Compressed.Clone()",
    "count > MaxExpandedBytes - read",
):
    assert required in wire, f"missing native transport cap: {required}"
assert "FirestormNavMeshCaps = false" in config
assert "Enabled = false" in config
print("OGL Firestorm NavMesh verified-source CAPS wiring: OK")
