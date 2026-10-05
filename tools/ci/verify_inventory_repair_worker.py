#!/usr/bin/env python3
from pathlib import Path

worker = Path("NexVerse/Server/Api/OglInventoryRepairWorker.cs").read_text(encoding="utf-8")
api = Path("NexVerse/Server/Api/OglJobsApi.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
docs = Path("doc/NexVerse/WORLD_API.md").read_text(encoding="utf-8")

required = [
    (worker, 'JobType => "inventory.repair"'),
    (worker, "IInventoryService"),
    (worker, "GetRootFolder(owner)"),
    (worker, "GetInventorySkeleton(owner)"),
    (worker, "MoveFolder(folder)"),
    (worker, 'OptionalBool(parameters, "dry_run", true)'),
    (worker, "MaxRepairActions = 1000"),
    (worker, '"missing_parent"'),
    (worker, '"self_parent"'),
    (worker, '"parent_cycle"'),
    (worker, '["deletions"] = bool.FalseString'),
    (worker, '["direct_database_access"] = bool.FalseString'),
    (api, '"/api/v1/jobs/inventory/repair"'),
    (api, '"inventory.repair"'),
    (api, "NexScopes.AdminAll"),
    (connector, "new OglInventoryRepairWorker(inventory)"),
    (roadmap, "- [x] inventory repair;"),
    (roadmap, "`addons.opengenesislink.de`"),
    (docs, "Persistent Job Engine: Inventory repair"),
    (docs, "`admin:*`"),
]

missing = [needle for text, needle in required if needle not in text]
if missing:
    raise SystemExit("Inventory repair worker missing: " + ", ".join(missing))

for banned in (
    "DeleteFolders(",
    "DeleteItems(",
    "PurgeFolder(",
    "IXInventoryData",
    "OpenSim.Data",
):
    if banned in worker:
        raise SystemExit("Inventory repair worker contains forbidden destructive/direct datastore path: " + banned)

print("OpenGenesisLINK inventory repair worker: OK")
