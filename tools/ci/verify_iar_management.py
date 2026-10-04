#!/usr/bin/env python3
from pathlib import Path

mgmt=Path("NexVerse/RegionModules/Archives/OglIarManagement.cs").read_text()
ops=Path("NexVerse/RegionModules/Archives/OglIarOperationManager.cs").read_text()
mod=Path("NexVerse/RegionModules/Archives/OglIarManagementModule.cs").read_text()
required=[
(mgmt,"class OglIarInspector"),(mgmt,'"inventory/"'),(mgmt,'"assets/"'),(mgmt,"SHA256.Create()"),
(mgmt,"class OglIarStoragePolicy"),(ops,"IInventoryArchiverModule"),(ops,"ArchiveInventory("),
(ops,"DearchiveInventory("),(ops,'["merge"] = merge'),(ops,"OnInventoryArchiveSaved"),
(ops,"OnInventoryArchiveLoaded"),(ops,"InventoryPath"),(mod,'Configs["OpenGenesisLINKIAR"]'),
(mod,"RegionLoaded(Scene scene)"),(mod,"RegisterModuleInterface<IOglIarOperations>")
]
missing=[n for t,n in required if n not in t]
if missing: raise SystemExit("IAR management foundation missing: "+", ".join(missing))
if "InventoryArchiveWriteRequest" in ops or "InventoryArchiveReadRequest" in ops:
    raise SystemExit("OGL IAR manager must not duplicate original archiver internals")
print("OpenGenesisLINK IAR management foundation: OK")
