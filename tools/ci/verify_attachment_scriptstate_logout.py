#!/usr/bin/env python3
from pathlib import Path

group = Path(
    "OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs"
).read_text(encoding="utf-8")

inventory = Path(
    "OpenSim/Region/Framework/Scenes/SceneObjectPartInventory.cs"
).read_text(encoding="utf-8")

serializer = Path(
    "OpenSim/Region/Framework/Scenes/Serialization/SceneObjectSerializer.cs"
).read_text(encoding="utf-8")

attachments = Path(
    "OpenSim/Region/CoreModules/Avatar/Attachments/AttachmentsModule.cs"
).read_text(encoding="utf-8")

for marker in (
    "SceneObjectPart part = parts[i];",
    "IEntityInventory inventory = part?.Inventory;",
    "if (inventory is null)",
    "Dictionary<UUID, string> pstates =",
    "if (pstates is null || pstates.Count == 0)",
):
    assert marker in group, (
        "missing attachment script-state null guard: " + marker
    )

for marker in (
    "SceneObjectPart part = m_part;",
    "TaskInventoryDictionary items = m_items;",
    "if (disposed || part is null || items is null)",
    "SceneObjectGroup group = part.ParentGroup;",
    "Scene scene = group?.Scene;",
    "if (scene is null)",
    "if (scriptEngines is null || scriptEngines.Length == 0)",
    "finally",
    "items.LockItemsForRead(false);",
    "if (item is null)",
):
    assert marker in inventory, (
        "missing disposed inventory/script-state guard: " + marker
    )

for marker in (
    "sop.ParentGroup?.Scene",
    "SceneObjectPartInventory inventory =",
    "(inventory?.Serial ?? 0).ToString()",
    "inventory?.Items",
    "if (tinv == null || tinv.Count == 0)",
    "scene != null",
    "if (item == null)",
):
    assert marker in serializer, (
        "missing detached attachment serializer guard: " + marker
    )

for marker in (
    "SceneObjectPart root = grp?.RootPart;",
    "if (root == null || root.Inventory == null)",
    "Attachment asset update skipped because the root part or its inventory was already disposed.",
):
    assert marker in attachments, (
        "missing attachment asset-save guard: " + marker
    )

print("Attachment logout script-state and serializer safety contract: OK")
