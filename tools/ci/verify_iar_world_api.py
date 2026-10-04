#!/usr/bin/env python3
from pathlib import Path
api=Path("NexVerse/Server/Api/OglIarApi.cs").read_text()
node=Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text()
ops=Path("NexVerse/RegionModules/Archives/OglIarOperationManager.cs").read_text()
iface=Path("OpenSim/Region/Framework/Interfaces/IInventoryArchiverModule.cs").read_text()
impl=Path("OpenSim/Region/CoreModules/Avatar/Inventory/Archiver/InventoryArchiverModule.cs").read_text()
connector=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
req=[(api,"/api/v1/iar/export"),(api,"/api/v1/iar/import"),(api,"/api/v1/iar/operations/"),(api,"NexScopes.RegionsManage"),(api,'"user_id"'),(api,'"archive.iar.requested"'),(node,'"archive.iar.requested"'),(node,"RequestModuleInterface<IOglIarOperations>"),(node,"GetUserAccount(scene.RegionInfo.ScopeID, userId)"),(ops,"UserAccount user"),(iface,"UUID id, UserAccount userInfo"),(impl,"UUID id, UserAccount userInfo"),(connector,'"/api/v1/iar"')]
missing=[n for t,n in req if n not in t]
if missing: raise SystemExit("IAR World API missing: "+", ".join(missing))
for text,name in [(api,"API"),(node,"NodeAgent")]:
    if '"password"' in text or '"pass"' in text: raise SystemExit(name+" must not transport IAR user credentials")
if "InventoryArchiveWriteRequest" in ops or "InventoryArchiveReadRequest" in ops: raise SystemExit("Managed IAR layer duplicated legacy internals")
print("OpenGenesisLINK IAR World API: OK")
