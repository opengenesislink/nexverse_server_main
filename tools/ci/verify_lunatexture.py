#!/usr/bin/env python3
from pathlib import Path

warp = Path("OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs").read_text(encoding="utf-8")
defaults = Path("bin/OpenSimDefaults.ini").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")

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
    "RecordLunaTextureDiagnostic",
    '[LunaTexture]: missing texture',
    'LunaTextureRasterFallback',
    'Image.FromStream',
)
for marker in required_warp:
    assert marker in warp, f"missing LunaTexture implementation marker: {marker}"

assert "LunaTextureEnabled = true" in defaults
assert "LunaTextureRasterFallback = true" in defaults
assert "LunaTexturePlaceholder = true" in defaults
assert "LunaTextureDiagnosticLimit = 256" in defaults
assert "authoritative asset is never rewritten" in defaults
assert "Phase 1 implementation status" in roadmap
assert "non-destructive raster decoder fallback" in roadmap

print("LunaTexture phase 1 contract: OK")
