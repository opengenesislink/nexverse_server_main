#!/usr/bin/env python3
"""Guard the source inventory lifecycle and aligned attachment teleport state.

The live avatar TP from Osnabrueck to Karlsruhe previously dereferenced a
disposed SceneObjectPart inventory on SceneObjectPart.Copy() line 2139.
This is a source-contract regression; release build and live TP remain separate.
"""

from pathlib import Path

part = Path("OpenSim/Region/Framework/Scenes/SceneObjectPart.cs").read_text(encoding="utf-8")
group = Path("OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs").read_text(encoding="utf-8")
attachments = Path("OpenSim/Region/CoreModules/Avatar/Attachments/AttachmentsModule.cs").read_text(encoding="utf-8")
workflow = Path(".github/workflows/nexjast-ci.yml").read_text(encoding="utf-8")

copy = part.split("public SceneObjectPart Copy(", 1)[1].split(
    "/// <summary>", 1
)[0]
for marker in (
    "SceneObjectPartInventory sourceInventory = m_inventory;",
    "if (disposed || sourceInventory is null)",
    "lock (sourceInventory)",
    "if (disposed || sourceItems is null)",
    "throw new ObjectDisposedException(nameof(SceneObjectPart)",
    "copiedItems = (TaskInventoryDictionary)sourceItems.Clone();",
    "dupe.m_inventory = new SceneObjectPartInventory(dupe);",
    "dupe.m_inventory.Items = copiedItems;",
    "dupe.m_inventory.HasInventoryChanged = inventoryChanged;",
):
    assert marker in copy, f"missing safe inventory snapshot marker: {marker}"
assert copy.index("copiedItems = (TaskInventoryDictionary)sourceItems.Clone();") < copy.index(
    "SceneObjectPart dupe = (SceneObjectPart)MemberwiseClone();"
), "source inventory must be validated and cloned before MemberwiseClone"
assert copy.index("dupe.m_inventory = new SceneObjectPartInventory(dupe);") < copy.index(
    "dupe.m_shape = m_shape.Copy();"
), "cloned part must not inherit source inventory during partially failed copy"

dispose = part.split("protected void Dispose(bool disposing)", 1)[1].split(
    "#endregion Constructors", 1
)[0]
for marker in (
    "SceneObjectPartInventory inventory = m_inventory;",
    "lock (inventory)",
    "ReferenceEquals(m_inventory, inventory)",
    "inventory.Dispose();",
    "m_inventory = null;",
):
    assert marker in dispose, f"missing disposal coordination marker: {marker}"

copy_group = group.split("public SceneObjectGroup Copy(bool userExposed)", 1)[1].split(
    "public void CopyRootPart(", 1
)[0]
assert "try" in copy_group and "finally" in copy_group
assert "m_dupeInProgress = false;" in copy_group
assert "dupe.m_dupeInProgress = false;" in copy_group

outbound = attachments.split("public void CopyAttachments(IScenePresence sp, AgentData ad)", 1)[1].split(
    "public void CopyAttachments(AgentData ad, IScenePresence isp)", 1
)[0]
for marker in (
    "sog.IsDeleted",
    "sog.RootPart.IsDeleted",
    "sog.RootPart.Inventory is null",
    "catch (ObjectDisposedException e)",
    "catch (NullReferenceException e)",
    "SceneObjectGroup clone = (SceneObjectGroup)sog.CloneForNewScene();",
    "string state = sog.GetStateSnapshot();",
    "ad.AttachmentObjects.Add(clone);",
    "ad.AttachmentObjectStates.Add(state);",
    "sp.InTransitScriptStates.Add(state);",
):
    assert marker in outbound, f"missing teleport safeguard: {marker}"
assert outbound.index("string state = sog.GetStateSnapshot();") < outbound.index(
    "ad.AttachmentObjects.Add(clone);"
), "attachment clone/state must finish before updating transfer lists"
assert "new TaskInventoryDictionary()" not in outbound, "never silently erase attachment inventory"
assert "python3 tools/ci/verify_attachment_teleport_clone.py" in workflow

print("Attachment teleport disposed-inventory clone guard: OK")
