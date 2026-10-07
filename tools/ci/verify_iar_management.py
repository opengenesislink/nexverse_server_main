#!/usr/bin/env python3
from pathlib import Path

mgmt=Path("NexVerse/RegionModules/Archives/OglIarManagement.cs").read_text()
ops=Path("NexVerse/RegionModules/Archives/OglIarOperationManager.cs").read_text()
mod=Path("NexVerse/RegionModules/Archives/OglIarManagementModule.cs").read_text()
archiver=Path("OpenSim/Region/CoreModules/Avatar/Inventory/Archiver/InventoryArchiverModule.cs").read_text()
required=[
(mgmt,"class OglIarInspector"),(mgmt,'"inventory/"'),(mgmt,'"assets/"'),(mgmt,"SHA256.Create()"),
(mgmt,"class OglIarStoragePolicy"),(ops,"IInventoryArchiverModule"),(ops,"ArchiveInventory("),
(ops,"DearchiveInventory("),(ops,'["merge"] = merge'),(ops,"OnInventoryArchiveSaved"),
(ops,"OnInventoryArchiveLoaded"),(ops,"InventoryPath"),(mod,'Configs["OpenGenesisLINKIAR"]'),
(mod,"RegionLoaded(Scene scene)"),(mod,"RegisterModuleInterface<IOglIarOperations>"),
(archiver,"scene.RegisterModuleInterface<IInventoryArchiverModule>(this);"),
(archiver,"scene.UnregisterModuleInterface<IInventoryArchiverModule>(this);"),
(archiver,"m_scenes.Remove(scene.RegionInfo.RegionID)")
]
missing=[n for t,n in required if n not in t]
if missing: raise SystemExit("IAR management foundation missing: "+", ".join(missing))
if "InventoryArchiveWriteRequest" in ops or "InventoryArchiveReadRequest" in ops:
    raise SystemExit("OGL IAR manager must not duplicate original archiver internals")
print("OpenGenesisLINK IAR management foundation: OK")

register_pos=archiver.find("scene.RegisterModuleInterface<IInventoryArchiverModule>(this);")
first_region_pos=archiver.find("if (m_scenes.Count == 0)")
if register_pos < 0 or first_region_pos < 0 or register_pos > first_region_pos:
    raise SystemExit("IInventoryArchiverModule must be registered on every region before first-region-only setup")
