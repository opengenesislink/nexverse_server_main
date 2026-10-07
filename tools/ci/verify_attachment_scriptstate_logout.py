#!/usr/bin/env python3
from pathlib import Path

group = Path(
    "OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs"
).read_text(encoding="utf-8")

inventory = Path(
    "OpenSim/Region/Framework/Scenes/SceneObjectPartInventory.cs"
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

print("Attachment logout script-state safety contract: OK")
