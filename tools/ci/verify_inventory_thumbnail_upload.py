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
    "circuit.SessionID != sp.ControllingClient.SessionId",
    "TeleportFlags.ViaHGLogin",
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
assert "if (linked && m_Folder != UUID.Zero)" in mod
assert "verified.ThumbnailID != persisted" in mod
assert "Folder {0} update acknowledged but thumbnail {1} not confirmed" in mod
item = (root/"OpenSim/Framework/InventoryItemBase.cs").read_text()
folders = (root/"OpenSim/Capabilities/Handlers/FetchInventory/FetchInvDescHandler.cs").read_text()
login = (root/"OpenSim/Services/LLLoginService/LLLoginResponse.cs").read_text()
# All diagnostics must be opt-in for a single folder; never log full inventory
# or dump entire LLSD responses containing private inventory details.
for source, marker in [
    (mod, "UPLOAD_BEFORE"),
    (mod, "UPLOAD_AFTER"),
    (login, "LOGIN_SKELETON"),
    (folders, "FETCH_CHILD"),
    (folders, "FETCH_SELF"),
]:
    assert "OGL_THUMBNAIL_TRACE_FOLDER_ID" in source, "unscoped thumbnail diagnostics"
    assert marker in source, f"missing trace event: {marker}"
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
# Test the actual SQLite migration on an existing (populated) inventory
# schema, not just the spelling of its columns.
import sqlite3
schema=(root/"OpenSim/Data/SQLite/Resources/XInventoryStore.migrations").read_text()
initial=schema.split(":VERSION 1",1)[1].split(":VERSION 2",1)[0]
upgrade=schema.split(":VERSION 3",1)[1]
db=sqlite3.connect(":memory:")
db.executescript(initial)
db.execute("INSERT INTO inventoryfolders (folderName, type, version, folderID, agentID, parentFolderID) VALUES ('My Outfits', 48, 1, ?, ?, ?)",
    ("10000000-0000-4000-8000-000000000001",
     "20000000-0000-4000-8000-000000000002",
     "30000000-0000-4000-8000-000000000003"))
db.execute("INSERT INTO inventoryitems (assetID, assetType, inventoryName, inventoryID, avatarID, parentFolderID) VALUES (?, 0, 'Shirt', ?, ?, ?)",
    ("40000000-0000-4000-8000-000000000004",
     "50000000-0000-4000-8000-000000000005",
     "20000000-0000-4000-8000-000000000002",
     "10000000-0000-4000-8000-000000000001"))
db.commit()
db.executescript(upgrade)
preview="60000000-0000-4000-8000-000000000006"
db.execute("UPDATE inventoryfolders SET thumbnailID=?", (preview,))
db.execute("UPDATE inventoryitems SET thumbnailID=?", (preview,))
assert db.execute("SELECT folderName, thumbnailID FROM inventoryfolders").fetchone()==("My Outfits",preview)
assert db.execute("SELECT inventoryName, thumbnailID FROM inventoryitems").fetchone()==("Shirt",preview)
db.close()
print("Firestorm InventoryThumbnailUpload two-phase JP2 upload and secure storage: OK")
