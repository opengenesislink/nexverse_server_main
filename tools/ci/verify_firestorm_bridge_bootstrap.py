#!/usr/bin/env python3
from pathlib import Path

def require(path, needle):
    text = Path(path).read_text(encoding="utf-8")
    assert needle in text, f"{path}: missing {needle!r}"
    return text

compat = require(
    "OpenSim/Framework/FirestormBridgeCompatibility.cs",
    'public const string DefaultBridgeVersion = "2.29";'
)
for needle in (
    'RootFolderName = "#Firestorm"',
    'BridgeFolderName = "#LSL Bridge"',
    '#Firestorm LSL Bridge v',
    'BootstrapAssetId',
    'IsFirestormViewer',
):
    assert needle in compat, f"compatibility constants missing {needle}"

login = require(
    "OpenSim/Services/LLLoginService/LLLoginService.cs",
    "EnsureFirestormBridgeInventory(account.PrincipalID)"
)
for needle in (
    "EnableFirestormBridgeBootstrap",
    "FirestormBridgeVersion",
    "FirestormBridgeCompatibility.BootstrapAssetId",
    "(int)InventoryType.Object",
):
    assert needle in login, f"LLLoginService missing {needle}"

module = require(
    "OpenSim/Region/CoreModules/Framework/ViewerSupport/FirestormBridgeAssetBootstrapModule.cs",
    "SceneObjectSerializer.ToOriginalXmlFormat"
)
for needle in (
    "FirestormBridgeCompatibility.BootstrapAssetId",
    "PrimitiveBaseShape.CreateBox()",
    "NextOwnerMask = all",
):
    assert needle in module, f"asset bootstrap module missing {needle}"

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example"):
    text = require(path, "[FirestormBridge]")
    assert "Enabled = true" in text.split("[FirestormBridge]", 1)[1].split("[", 1)[0], (
        f"{path}: FirestormBridge must be enabled"
    )

for path in ("bin/Robust.HG.ini", "bin/Robust.HG.ini.example"):
    text = require(path, "EnableFirestormBridgeBootstrap = true")
    assert 'FirestormBridgeVersion = "2.29"' in text
    assert 'LibraryName = "Bibiothek"' in text
    assert 'LibraryName = "OpenSim Library"' not in text

library = require(
    "OpenSim/Services/InventoryService/LibraryService.cs",
    'string pLibName = "Bibiothek";'
)
assert '"OpenSim Library"' not in library

print("OpenGenesisLINK Firestorm bridge bootstrap + Bibiothek configuration: OK")
