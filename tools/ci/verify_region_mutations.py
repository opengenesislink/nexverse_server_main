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

host = require(
    "NexVerse/RegionModules/NodeAgent/NexVerseManagedRegionHostPlugin.cs",
    "ManagedRegionCommands",
    "scene.GetRootAgentCount() > 0",
    "m_OpenSim.CloseRegion(",
    "region_not_nexverse_managed",
    "attempting rollback",
    "NexVerseNonFatalGridRegistration",
)

if "m_OpenSim.RemoveRegion(" in host:
    errors.append("managed region host must not call RemoveRegion because it deletes scene objects")

node = require(
    "NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs",
    '"region.control.create.requested"',
    '"region.control.move.requested"',
    '"region.control.operation.accepted"',
    '"region.control.operation.completed"',
    '"region.control.operation.failed"',
    '"target_node_id"',
    '"managed_region_commands"',
)

api = require(
    "NexVerse/Server/Api/NexRegionMutationApi.cs",
    "NexScopes.RegionsManage",
    '"region.control.create.requested"',
    '"region.control.move.requested"',
    "Idempotency-Key",
    "node.ManagedRegionCommands",
    "region_has_agents",
)

handlers = require(
    "NexVerse/Server/Api/NexVerseWorldApiHandlers.cs",
    '("/api/v1/regions"',
    '"regions:manage"',
    '"/api/v1/regions/{regionId}/placement"',
    '"/api/v1/region-operations/{operationId}"',
    '"RegionCreateRequest"',
    '"RegionPlacementRequest"',
    '"RegionOperation"',
)

for config in (
    "bin/OpenSim.ini",
    "bin/OpenSim.ini.example",
    "bin/OpenSimDefaults.ini",
):
    text = require(
        config,
        "ManagedRegionCommands = false",
        'ManagedRegionConfigDirectory = "Regions/NexVerseManaged"',
        "ManagedRegionPortMin = 9300",
        "ManagedRegionPortMax = 9399",
    )

opensim = require(
    "OpenSim/Region/Application/OpenSimBase.cs",
    "NexVerseNonFatalGridRegistration",
    "Environment.Exit(1);",
)

if errors:
    print("[NX-REGION-MUTATIONS] FEHLER")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("[NX-REGION-MUTATIONS] OK")
