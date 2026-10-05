#!/usr/bin/env python3
from pathlib import Path

warp = Path("OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs").read_text(encoding="utf-8")
defaults = Path("bin/OpenSimDefaults.ini").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
contract = Path("OpenSim/Region/Framework/Interfaces/ILunaTextureDiagnostics.cs").read_text(encoding="utf-8")
node_agent = Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8")
node_registry = Path("NexVerse/Core/ControlPlane/NexNodeRegistry.cs").read_text(encoding="utf-8")
node_api = Path("NexVerse/Server/Api/NexNodeApi.cs").read_text(encoding="utf-8")
world_api = Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")

required_warp = (
    'ClassifyTexturePayload',
    'TryDecodeRasterTexture',
    '"jpeg2000"',
    '"png"',
    '"jpeg"',
    '"gif"',
    '"bmp"',
    '"unknown"',
    "LunaTextureDiagnostic",
    "CreateLunaTexturePlaceholder",
    "nex texture status",
    "nex texture clear",
    "nex texture inspect",
    "nex texture retry",
    "ILunaTextureDiagnostics",
    "RetryTexture",
    "RecordLunaTextureDiagnostic",
    '[LunaTexture]: missing texture',
    'LunaTextureRasterFallback',
    'Image.FromStream',
    'LunaTextureDiagnosticStore',
    'LoadLunaTextureDiagnostics',
    'PersistLunaTextureDiagnostics',
    'File.Move(',
    'DeleteLunaTextureDiagnosticStore',
)
for marker in required_warp:
    assert marker in warp, f"missing LunaTexture implementation marker: {marker}"

assert "ILunaTextureDiagnostics" in contract
assert "GetDiagnostics()" in contract
assert "TryGetDiagnostic" in contract
assert "RetryTexture" in contract
assert "LunaTextureEnabled = true" in defaults
assert "LunaTextureRasterFallback = true" in defaults
assert "LunaTexturePlaceholder = true" in defaults
assert "LunaTextureDiagnosticLimit = 256" in defaults
assert 'LunaTextureDiagnosticStore = "data/lunatexture"' in defaults
assert "authoritative asset is never rewritten" in defaults

for marker in (
    "CollectLunaTextureDiagnostics",
    '"luna_texture_diagnostic_count"',
    '"luna_texture_occurrence_count"',
    '"luna_texture_regions_affected"',
    '"luna_texture_last_seen_utc"',
    '"luna_texture_classifications_json"',
):
    assert marker in node_agent, f"missing NodeAgent LunaTexture marker: {marker}"

for marker in (
    "LunaTextureDiagnosticCount",
    "LunaTextureOccurrenceCount",
    "LunaTextureRegionsAffected",
    "LunaTextureLastSeen",
    "LunaTextureClassifications",
    "AssignLongDictionary",
):
    assert marker in node_registry, f"missing node-registry LunaTexture marker: {marker}"

assert "LunaTextureAggregatePayload" in node_api
assert "LunaTextureNodePayload" in node_api
assert 'luna_texture =' in node_api
assert '["LunaTextureDiagnosticsSummary"]' in world_api
assert 'SchemaRef("LunaTextureDiagnosticsSummary")' in world_api

assert "Phase 1 implementation status" in roadmap
assert "non-destructive raster decoder fallback" in roadmap

print("LunaTexture persistent diagnostics + World API projection: OK")
