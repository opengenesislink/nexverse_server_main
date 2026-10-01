#!/usr/bin/env python3
"""Verify the mandatory NexVerse simulator runtime profile."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]

EXPECTED = {
    ("Startup", "physics"): "ubODE",
    ("Startup", "meshing"): "ubODEMeshmerizer",
    ("Map", "GenerateMaptiles"): "true",
    ("Map", "MapImageModule"): "Warp3DImageModule",
    ("Map", "DrawPrimOnMapTile"): "true",
    ("Map", "TextureOnMapTile"): "true",
    ("Map", "TexturePrims"): "true",
    ("Map", "RenderMeshes"): "true",
    ("XMLRPC", "XmlRpcRouterModule"): "XmlRpcRouterModule",
    ("XMLRPC", "XmlRpcPort"): "20800",
    ("Mesh", "UseMeshiesPhysicsMesh"): "true",
    ("Mesh", "ConvexPrims"): "true",
    ("Mesh", "ConvexSculpts"): "true",
    ("Mesh", "MeshFileCache"): "true",
    ("Mesh", "MeshFileCachePath"): "MeshCache",
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
    "OpenSim/Region/PhysicsModules/ubOde/ODEModule.cs",
    "OpenSim/Region/PhysicsModules/ubOdeMeshing/Meshmerizer.cs",
    "OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs",
    "bin/OpenSim.Region.PhysicsModule.ubOde.dll.config",
    "bin/lib64/libubode-x86_64.so",
    "bin/lib64/libubode-arm64.so",
    "bin/lib64/libubode.dylib",
    "bin/lib64/ubode.dll",
)
for rel in required_files:
    if not (ROOT / rel).is_file():
        errors.append(f"required simulator runtime component missing: {rel}")

ode_prim = (ROOT / "OpenSim/Region/PhysicsModules/ubOde/ODEPrim.cs").read_text(encoding="utf-8")
create_geom_start = ode_prim.find("private void CreateGeom(bool OverrideToBox)")
create_geom_end = ode_prim.find("private void RemoveGeom()", create_geom_start)
if create_geom_start < 0 or create_geom_end < 0:
    errors.append("unable to locate ubODE CreateGeom collision fallback")
else:
    create_geom = ode_prim[create_geom_start:create_geom_end]
    if "bool meshUnavailable =" not in create_geom:
        errors.append("ubODE solid mesh-fallback guard is missing")
    if "m_NoColide = true;" in create_geom:
        errors.append("ubODE CreateGeom still disables collision on fallback geometry")

j2k_decoder = (ROOT / "OpenSim/Region/CoreModules/Agent/TextureSender/J2KDecoderModule.cs").read_text(encoding="utf-8")
if "DecodeToImageWithOpenJPEG" not in j2k_decoder or "CSJ2K and OpenJPEG" not in j2k_decoder:
    errors.append("J2K decoder must retain the CSJ2K -> OpenJPEG compatibility fallback")

warp3d = (ROOT / "OpenSim/Region/CoreModules/World/Warp3DMap/Warp3DImageModule.cs").read_text(encoding="utf-8")
if "m_textureDecodeWarnings" not in warp3d or "LogTextureDecodeFailureOnce" not in warp3d:
    errors.append("Warp3D must suppress repeated decode warnings for the same texture UUID")

launcher = (ROOT / "bin/opensim.sh").read_text(encoding="utf-8")
match = re.search(r"^\s*ulimit\s+-s\s+(\d+)\s*$", launcher, re.MULTILINE)
if match is None or int(match.group(1)) < 262144:
    errors.append("bin/opensim.sh must set an ubODE-safe stack limit of at least 262144 KiB")

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print("NexVerse simulator runtime profile verified: ubODE + solid mesh fallback + ubODEMeshmerizer + Warp3D JPEG2000 fallback + LSL XML-RPC RemoteData.")
