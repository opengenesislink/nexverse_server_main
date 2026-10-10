#!/usr/bin/env python3
"""Strict native viewer-only transport, never claim Firestorm/Havok compatibility."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
graph = (root / "NexVerse/Core/Pathfinding/OglLayeredNavGraph.cs").read_text()
codec = (root / "NexVerse/Core/Pathfinding/OglOpenNavGraphCodec.cs").read_text()
region = (root / "NexVerse/RegionModules/Pathfinding/OglTerrainNavigationModule.cs").read_text()
caps = (root / "NexVerse/RegionModules/Pathfinding/OglOpenNavGraphCapsModule.cs").read_text()
config = (root / "bin/OpenSim.ini.example").read_text()
for item in ("TryCaptureCertifiedArcs", "m_VerifiedPlanarEdges == null",
             "new OglOpenNavArc(from, to, true)", "result.Sort("):
    assert item in graph, item
for item in ("SHA256.HashData", "FixedTimeEquals", "FormatVersion = 1",
             "MaxBytes = 12 * 1024 * 1024", "MaxNodes = 65536",
             "MaxArcs = 600000", "TryDecode(",
             "Only a current physics-certified graph is exportable"):
    assert item in codec, item
for item in ("TryCaptureCertifiedGraph(", "IsNavigationDirty",
             "SnapshotEpoch", "TryCaptureCertifiedArcs",
             "RegisterModuleInterface<IOglCertifiedNavGraphRegion>"):
    assert item in region, item
for item in ('"OGLRetrieveNavGraph"', '"OGLNAVGRAPH/1"',
             '"OpenNavGraphCaps", false', 'response.AddHeader("Cache-Control", "no-store")',
             "TryGetScenePresence(session.Avatar",
             "TryCaptureCertifiedGraph(", "ReferenceEquals(current, graph)",
             "HttpStatusCode.TooManyRequests", 'OSD.FromBinary(payload)'):
    assert item in caps, item
assert '"RetrieveNavMeshSrc"' not in caps
assert "OpenNavGraphCaps = false" in config
assert "FirestormNavMeshCaps = false" in config
print("OGL native NavGraph/1 CAPS and transport guard: OK")
