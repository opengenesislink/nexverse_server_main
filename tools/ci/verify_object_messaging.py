#!/usr/bin/env python3
from pathlib import Path

router = Path("OpenSim/Region/Framework/Interfaces/ICrossRegionObjectMessageRouter.cs").read_text(encoding="utf-8")
lsl = Path("OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs").read_text(encoding="utf-8")
node = Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8")
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
    "m_AllowCrossOwnerObjectMessages",
    "targetPart.OwnerID",
    "sourcePart.OwnerID != sourceOwnerId",
    "message.Length > 1023",
):
    assert marker in node, f"missing NodeAgent object messaging marker: {marker}"

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example", "bin/OpenSimDefaults.ini"):
    config = Path(path).read_text(encoding="utf-8")
    assert "CrossRegionObjectMessaging = false" in config
    assert "AllowCrossOwnerObjectMessages = false" in config
    assert "ObjectMessagesPerSecond = 20" in config
    assert "ObjectMessageMaxAgeSeconds = 30" in config

assert "### 9.6 Cross-region object-to-object communication" in roadmap
print("Cross-region object messaging contract: OK")
