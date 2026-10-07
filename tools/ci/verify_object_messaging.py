#!/usr/bin/env python3
from pathlib import Path

router = Path("OpenSim/Region/Framework/Interfaces/ICrossRegionObjectMessageRouter.cs").read_text(encoding="utf-8")
lsl = Path("OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs").read_text(encoding="utf-8")
node = Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8")
registry = Path("NexVerse/Core/ControlPlane/NexNodeRegistry.cs").read_text(encoding="utf-8")
node_api = Path("NexVerse/Server/Api/NexNodeApi.cs").read_text(encoding="utf-8")
world_api = Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")

for marker in (
    "interface ICrossRegionObjectMessageRouter",
    "bool TryRoute(",
):
    assert marker in router, f"missing cross-region router marker: {marker}"

for marker in (
    "llRegionSayTo",
    "ICrossRegionObjectMessageRouter",
    "World.GetScenePresence(TargetID)",
    "World.GetSceneObjectPart(TargetID)",
    "router?.TryRoute(",
):
    assert marker in lsl, f"missing LSL remote fallback marker: {marker}"

for marker in (
    "ICrossRegionObjectMessageRouter",
    'CrossRegionObjectMessaging',
    'AllowCrossOwnerObjectMessages',
    'ObjectMessagesPerSecond',
    'ObjectMessageMaxAgeSeconds',
    '"object.message.requested"',
    '"object.message.received"',
    "TryTakeObjectMessageRateSlot",
    "ObjectMessageRateEntryLimit = 8192",
    '"nexverse.simulator"',
    "m_AllowCrossOwnerObjectMessages",
    "targetPart.OwnerID",
    "sourcePart.OwnerID != sourceOwnerId",
    "message.Length > 1023",
):
    assert marker in node, f"missing NodeAgent object messaging marker: {marker}"

for marker in (
    "CrossRegionObjectMessaging",
    "CrossOwnerObjectMessaging",
    "ObjectMessagesPerSecond",
    "ObjectMessageMaxAgeSeconds",
    '"cross_region_object_messaging"',
    '"cross_owner_object_messaging"',
    '"object_messages_per_second"',
    '"object_message_max_age_seconds"',
):
    assert marker in registry, f"missing Node registry object messaging projection: {marker}"

for marker in (
    "node.CrossRegionObjectMessaging",
    "node.CrossOwnerObjectMessaging",
    "node.ObjectMessagesPerSecond",
    "node.ObjectMessageMaxAgeSeconds",
    "cross_region_object_messaging =",
    "cross_owner_object_messaging =",
    "object_messages_per_second =",
    "object_message_max_age_seconds =",
    "accepting_remote_object_messages =",
):
    assert marker in node_api, f"missing Node API object messaging projection: {marker}"

for marker in (
    '["cross_region_object_messaging"]',
    '["cross_owner_object_messaging"]',
    '["object_messages_per_second"]',
    '["object_message_max_age_seconds"]',
):
    assert marker in world_api, f"missing Node OpenAPI object messaging marker: {marker}"

# The NexVerse deployment opts in to cross-owner and cross-region delivery.
# The shipped OpenSimDefaults.ini must retain conservative defaults for other installations.
for path, enabled in (
    ("bin/OpenSim.ini", True),
    ("bin/OpenSim.ini.example", True),
    ("bin/OpenSimDefaults.ini", False),
):
    config = Path(path).read_text(encoding="utf-8")
    state = "true" if enabled else "false"
    assert f"CrossRegionObjectMessaging = {state}" in config, path
    assert f"AllowCrossOwnerObjectMessages = {state}" in config, path
    assert "ObjectMessagesPerSecond = 20" in config, path
    assert "ObjectMessageMaxAgeSeconds = 30" in config, path

assert "### 9.6 Cross-region object-to-object communication" in roadmap
print("Cross-region object messaging contract: OK")
