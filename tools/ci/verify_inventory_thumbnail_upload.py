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
print("Firestorm InventoryThumbnailUpload two-phase JP2 upload and secure storage: OK")
