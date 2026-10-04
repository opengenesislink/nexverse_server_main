#!/usr/bin/env python3
from pathlib import Path

manager = Path("NexVerse/RegionModules/Archives/OglOarOperationManager.cs").read_text(encoding="utf-8")
module = Path("NexVerse/RegionModules/Archives/OglOarManagementModule.cs").read_text(encoding="utf-8")
required_manager = [
    "interface IOglOarOperations",
    "StartExport",
    "StartImport",
    "IRegionArchiverModule",
    "ArchiveRegion(path, requestId",
    "DearchiveRegion(path, requestId",
    "OnOarFileSaved",
    "OnOarFileLoaded",
    "OglOarOperationState.Completed",
    "OglOarOperationState.Failed",
    "OglOarInspector.Inspect",
]
required_module = [
    'Configs["OpenGenesisLINKOAR"]',
    "StorageRoot",
    "MaximumArchiveMiB",
    "RegisterModuleInterface<IOglOarOperations>",
    "UnregisterModuleInterface<IOglOarOperations>(manager)",
]
missing = [x for x in required_manager if x not in manager] + [x for x in required_module if x not in module]
if missing:
    raise SystemExit("OAR operation layer missing: " + ", ".join(missing))

if "new ArchiveWriteRequest" in manager or "new ArchiveReadRequest" in manager:
    raise SystemExit("OGL OAR manager must use IRegionArchiverModule instead of duplicating legacy archiver internals")

print("OpenGenesisLINK OAR operation layer: OK")
