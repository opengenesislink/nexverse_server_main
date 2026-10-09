#!/usr/bin/env python3
"""Firestorm inventory thumbnail upload CAP security/protocol regression guard."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
mod = (root/"OpenSim/Region/ClientStack/Linden/Caps/InventoryThumbnailUploadModule.cs").read_text()
proj = (root/"OpenSim/Region/ClientStack/Linden/Caps/OpenSim.Region.ClientStack.LindenCaps.csproj").read_text()
cfg = (root/"bin/OpenSimDefaults.ini").read_text()
for symbol in (
    "InventoryThumbnailUploadModule", 'RegisterSimpleHandler("InventoryThumbnailUpload"',
    '"item_id"', '"category_id"', "ownItem.Owner == agent",
    "ownFolder.Owner == agent", "sp.IsChildAgent",
    '"uploader"', '"state"', 'OSD.FromString("upload")',
    "SimpleBinaryHandler", "MaxDataSize = m_MaxBytes",
    "m_Timer = new Timer", "TimeSpan.FromSeconds(60)",
    "Interlocked.Exchange(ref m_Consumed, 1)",
    "m_Remote.Equals(request.RemoteIPEndPoint.Address)",
    "IsJpeg2000(content)", "AssetType.Texture",
    "scene.AssetService.Store(asset)", "Temporary = false",
    "Local = false", 'OSD.FromString("complete")',
    '"new_asset"', "RemoveSimpleStreamHandler(m_Path)"
):
    assert symbol in mod, f"missing upload/security marker {symbol}"
assert "InventoryThumbnailUploadModule.cs" in proj
assert 'Cap_InventoryThumbnailUpload = "localhost"' in cfg
assert "InventoryThumbnailUploadMaxBytes = 1048576" in cfg
assert 'Cap_InventoryThumbnailUpload = ""' not in cfg
models = [
    root/"OpenSim/Framework/InventoryItemBase.cs",
    root/"OpenSim/Framework/InventoryFolderBase.cs",
    root/"OpenSim/Data/IXInventoryData.cs",
]
for path in models:
    assert "ThumbnailID" in path.read_text() or "thumbnailID" in path.read_text(), path
store = (root/"OpenSim/Services/InventoryService/XInventoryService.cs").read_text()
assert store.count("ThumbnailID") >= 5
assert "check.ThumbnailID = folder.ThumbnailID" in store
assert "scene.InventoryService.UpdateItem(existing)" in mod
assert "scene.InventoryService.UpdateFolder(existing)" in mod
item = (root/"OpenSim/Framework/InventoryItemBase.cs").read_text()
folders = (root/"OpenSim/Capabilities/Handlers/FetchInventory/FetchInvDescHandler.cs").read_text()
for content in (item,folders):
    assert 'AddMap("thumbnail",' in content
    assert 'AddElem("asset_id",' in content
for path, version in [
    ("OpenSim/Data/MySQL/Resources/InventoryStore.migrations",8),
    ("OpenSim/Data/PGSQL/Resources/InventoryStore.migrations",11),
    ("OpenSim/Data/SQLite/Resources/XInventoryStore.migrations",3),
]:
    sql=(root/path).read_text()
    assert f":VERSION {version}" in sql
    assert "ADD COLUMN" in sql and "thumbnailID" in sql
for path in [
    "OpenSim/Services/Connectors/Inventory/XInventoryServicesConnector.cs",
    "OpenSim/Server/Handlers/Inventory/XInventoryInConnector.cs",
]:
    http=(root/path).read_text()
    assert http.count('"ThumbnailID"') >= 4, f"missing remote inventory roundtrip: {path}"
    assert "ThumbnailID" in http
print("Firestorm InventoryThumbnailUpload two-phase JP2 upload and secure storage: OK")
