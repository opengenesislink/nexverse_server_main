#!/usr/bin/env python3
from pathlib import Path

api = Path("NexVerse/Server/Api/OglOarApi.cs").read_text(encoding="utf-8")
node = Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8")
ops = Path("NexVerse/RegionModules/Archives/OglOarOperationManager.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")

required = [
    (api, "/api/v1/oar/export"),
    (api, "/api/v1/oar/import"),
    (api, "/api/v1/oar/operations/"),
    (api, "NexScopes.RegionsManage"),
    (api, "FindNodeForRegion"),
    (api, '"archive.oar.requested"'),
    (node, '"archive.oar.requested"'),
    (node, "RequestModuleInterface<IOglOarOperations>"),
    (node, "StartExport(operationId"),
    (node, "StartImport(operationId"),
    (node, '"archive.oar.operation." + state'),
    (ops, "event Action<OglOarOperation> OperationChanged"),
    (ops, "StartExport(Guid requestId"),
    (ops, "StartImport(Guid requestId"),
    (connector, '"/api/v1/oar"'),
    (connector, '"OpenGenesisLINK OAR API"'),
]
missing = [needle for text, needle in required if needle not in text]
if missing:
    raise SystemExit("OAR World API missing: " + ", ".join(missing))

if "ArchiveRegion(" in api or "DearchiveRegion(" in api:
    raise SystemExit("Robust OAR API must not call region archiver directly")

print("OpenGenesisLINK OAR World API: OK")
