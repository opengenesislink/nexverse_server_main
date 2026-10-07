#!/usr/bin/env python3
from pathlib import Path

lsl = Path("OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs").read_text(encoding="utf-8")
api = Path("OpenSim/Region/ScriptEngine/Shared/Api/Interface/ILSL_Api.cs").read_text(encoding="utf-8")
stub = Path("OpenSim/Region/ScriptEngine/Shared/Api/Runtime/LSL_Stub.cs").read_text(encoding="utf-8")
router = Path("OpenSim/Region/Framework/Interfaces/IEstateScriptMessageRouter.cs").read_text(encoding="utf-8")
node = Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")

assert "void llEstateSay(int channelID, string text);" in api
assert "public void llEstateSay(int channelID, string text)" in stub

for marker in (
    "public void llEstateSay(int channelID, string text)",
    'Error("llEstateSay", "Cannot use on channel 0")',
    "ScriptBaseClass.DEBUG_CHANNEL",
    "estate.IsEstateManagerOrOwner(ownerID)",
    "SceneManager.Instance",
    "targetEstate.EstateID != estateID",
    "IEstateScriptMessageRouter",
    "TryRouteEstateMessage(",
):
    assert marker in lsl, f"missing llEstateSay security/delivery marker: {marker}"

for marker in (
    "interface IEstateScriptMessageRouter",
    "TryRouteEstateMessage(",
):
    assert marker in router, f"missing estate router marker: {marker}"

for marker in (
    "IEstateScriptMessageRouter",
    "EstateScriptMessaging",
    '"estate.script.message.requested"',
    "sourceEstate.IsEstateManagerOrOwner(sourceOwnerId)",
    "targetEstate.IsEstateManagerOrOwner(sourceOwnerId)",
    "sourcePart.OwnerID != sourceOwnerId",
    "sourceEstate.EstateID != estateId",
    "targetEstate.EstateID != estateId",
    "message.Length > 1023",
):
    assert marker in node, f"missing estate script routing marker: {marker}"

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example", "bin/OpenSimDefaults.ini"):
    config = Path(path).read_text(encoding="utf-8")
    assert "EstateScriptMessaging = true" in config

assert "llEstateSay" in roadmap
print("Estate-wide LSL messaging contract: OK")
