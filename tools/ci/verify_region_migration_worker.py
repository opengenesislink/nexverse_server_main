#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
errors = []

def require(path, *needles):
    text = (ROOT / path).read_text(encoding="utf-8-sig")
    for needle in needles:
        if needle not in text:
            errors.append(f"{path}: missing {needle!r}")
    return text

worker = require(
    "NexVerse/Server/Api/OglRegionMigrationWorker.cs",
    'JobType => "regions.migrate"',
    "IOglNonCancellableJobWorker",
    "MigrationStorageId",
    '"archive.oar.requested"',
    '"region.control.lifecycle.requested"',
    '"region.control.create.requested"',
    '"retire"',
    "archive_sha256",
    "RollbackAsync",
    "WaitForRegionOnNodeAsync",
    "LoadEstateSettings(regionId, false)",
)

jobs = require(
    "NexVerse/Core/Jobs/OglJobWorkers.cs",
    "IOglNonCancellableJobWorker",
    '"job_not_cancellable_safely"',
)

api = require(
    "NexVerse/Server/Api/OglJobsApi.cs",
    '"/api/v1/jobs/regions/migrate"',
    '"regions.migrate"',
    '"target_node_id"',
    '"dry_run"',
)

connector = require(
    "NexVerse/Server/Api/NexVerseWorldApiConnector.cs",
    "new OglRegionMigrationWorker(",
    '"RegionMigrationOperationTimeoutSeconds"',
)

registry = require(
    "NexVerse/Core/ControlPlane/NexNodeRegistry.cs",
    "public string MigrationStorageId",
    '"migration_storage_id"',
)

node = require(
    "NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs",
    '"MigrationStorageId"',
    '["migration_storage_id"]',
    'action != "retire"',
    "TryRetireRegion(",
    '["archive_sha256"]',
    '["archive_valid"]',
)

host = require(
    "NexVerse/RegionModules/NodeAgent/NexVerseManagedRegionHostPlugin.cs",
    "TryRetireRegion(",
    '"region_still_running"',
    "File.Delete(configPath)",
)

for config in (
    "bin/OpenSim.ini",
    "bin/OpenSim.ini.example",
    "bin/OpenSimDefaults.ini",
):
    require(
        config,
        "[OpenGenesisLINKOAR]",
        'StorageRoot = "OGLArchives"',
        'MigrationStorageId = ""',
    )

docs = require(
    "doc/NexVerse/WORLD_API.md",
    "/api/v1/jobs/regions/migrate",
    "MigrationStorageId",
    "regions.migrate",
)

if "context.Progress(90, \"retiring_source\"" in worker:
    errors.append("source config is retired before final target verification")

if errors:
    print("[OGL-REGION-MIGRATION] FEHLER")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("[OGL-REGION-MIGRATION] OK")
