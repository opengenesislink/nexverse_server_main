#!/usr/bin/env python3
from pathlib import Path

src = Path("NexVerse/RegionModules/Archives/OglOarManagement.cs").read_text(encoding="utf-8")
required = [
    "SPDX-License-Identifier: MPL-2.0",
    "class OglOarInspection",
    "class OglOarInspector",
    'path.Equals("archive.xml", StringComparison.Ordinal)',
    "TryReadTarEntry",
    "SkipTarPayload",
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
region_start = prebuild.index('<Project name="NexVerse.RegionModules"')
region_end = prebuild.index("</Project>", region_start)
region_project = prebuild[region_start:region_end]
if '<Reference name="OpenSim.Region.CoreModules"/>' in region_project:
    raise SystemExit("OGL RegionModules must not introduce a circular CoreModules project dependency")

print("OpenGenesisLINK OAR management foundation: OK")
