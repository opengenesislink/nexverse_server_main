#!/usr/bin/env python3
"""Verify the mandatory NexVerse simulator runtime profile."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]

EXPECTED = {
    ("Startup", "physics"): "BulletSim",
    ("Startup", "meshing"): "Meshmerizer",
    ("Map", "GenerateMaptiles"): "true",
    ("Map", "MapImageModule"): "Warp3DImageModule",
    ("Map", "DrawPrimOnMapTile"): "true",
    ("Map", "TextureOnMapTile"): "true",
    ("Map", "TexturePrims"): "true",
    ("Map", "RenderMeshes"): "true",
    ("XMLRPC", "XmlRpcRouterModule"): "XmlRpcRouterModule",
    ("XMLRPC", "XmlRpcPort"): "20800",
}

def parse_ini(path: Path):
    section = None
    values = {}
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1].strip()
            continue
        if "=" not in line or section is None:
            continue
        key, value = line.split("=", 1)
        value = value.split(";", 1)[0].strip().strip('"')
        values[(section, key.strip())] = value
    return values

errors = []
for rel in ("bin/OpenSim.ini", "bin/OpenSim.ini.example", "bin/OpenSimDefaults.ini"):
    path = ROOT / rel
    values = parse_ini(path)
    for key, expected in EXPECTED.items():
        actual = values.get(key)
        if actual is None:
            errors.append(f"{rel}: missing active [{key[0]}] {key[1]}")
        elif actual.lower() != expected.lower():
            errors.append(f"{rel}: [{key[0]}] {key[1]}={actual!r}, expected {expected!r}")

required_files = (
    "OpenSim/Region/PhysicsModules/BulletS/BSScene.cs",
    "OpenSim/Region/PhysicsModules/Meshing/Meshmerizer/Meshmerizer.cs",
    "OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs",
)
for rel in required_files:
    if not (ROOT / rel).is_file():
        errors.append(f"required simulator runtime component missing: {rel}")

j2k_decoder = (ROOT / "OpenSim/Region/CoreModules/Agent/TextureSender/J2KDecoderModule.cs").read_text(encoding="utf-8")
if "DecodeToImageWithOpenJPEG" not in j2k_decoder or "CSJ2K and OpenJPEG" not in j2k_decoder:
    errors.append("J2K decoder must retain the CSJ2K -> OpenJPEG compatibility fallback")

warp3d = (ROOT / "OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs").read_text(encoding="utf-8")
if "m_textureDecodeWarnings" not in warp3d or "LogTextureDecodeFailureOnce" not in warp3d:
    errors.append("Warp3D must suppress repeated decode warnings for the same texture UUID")

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print("NexVerse simulator runtime profile verified: BulletSim + Meshmerizer + Warp3D JPEG2000 fallback + LSL XML-RPC RemoteData.")
