#!/usr/bin/env python3
from pathlib import Path

src = Path("NexVerse/RegionModules/Archives/OglOarManagement.cs").read_text(encoding="utf-8")
required = [
    "SPDX-License-Identifier: MPL-2.0",
    "class OglOarInspection",
    "class OglOarInspector",
    "ArchiveConstants.CONTROL_FILE_PATH",
    "TarArchiveReader",
    "SHA256.Create()",
    "class OglOarStoragePolicy",
    "MaximumArchiveBytes",
    "Path.GetFileName",
    "IRegionArchiverModule",
]
missing = [item for item in required if item not in src]
if missing:
    raise SystemExit("OAR management foundation missing: " + ", ".join(missing))

if "DearchiveRegion(" in src or "ArchiveRegion(" in src:
    raise SystemExit("Dry-run inspector must remain non-mutating")

prebuild = Path("prebuild.xml").read_text(encoding="utf-8")
if '<Reference name="OpenSim.Region.CoreModules"/>' not in prebuild:
    raise SystemExit("NexVerse.RegionModules must reference OpenSim.Region.CoreModules for the authoritative OAR serialization primitives")

print("OpenGenesisLINK OAR management foundation: OK")
