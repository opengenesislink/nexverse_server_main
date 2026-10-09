#!/usr/bin/env python3
from pathlib import Path

api = Path("NexVerse/Server/Api/NexInventoryApi.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
security = Path("NexVerse/Core/Security/NexSecurity.cs").read_text(encoding="utf-8")
world_api = Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")

for marker in (
    '"/api/v1/inventory/tree"',
    '"/api/v1/inventory/folders/"',
    '"/api/v1/inventory/items/"',
    "NexScopes.InventoryRead",
    "NexScopes.InventoryWrite",
    "AddFolder",
    "UpdateFolder",
    "MoveFolder",
    "UpdateItem",
    "MoveItems",
    "FolderType.Trash",
    "root_folder_protected",
    '"/api/v1/inventory/search"',
    '"/api/v1/inventory/lost-and-found"',
    '"/api/v1/inventory/trash/empty"',
    '"/api/v1/inventory/items/copy"',
    '"/api/v1/inventory/items"',
    '"/api/v1/inventory/links"',
    '"inventory_item_create_admin_required"',
    '"asset_service_unavailable"',
    "GetMetadata",
    "permission_masks_required",
    "AssetType.Link",
    "AssetType.LinkFolder",
    "inventory_link_chain_forbidden",
    "inventory_item_copy_forbidden",
    "inventory_folder_cycle",
    "WouldCreateFolderCycle",
    'request?.QueryString?["q"]',
    'request?.QueryString?["limit"]',
    "SearchFolderScanLimit = 2000",
    "SearchItemScanLimit = 10000",
    '"query_too_long"',
    '"invalid_optional_mask"',
    "DefaultInventoryTypeForAsset",
    "IsInventoryTypeCompatible",
    '"folder_not_in_trash"',
    '"item_not_in_trash"',
    "PurgeFolder",
    "Clone()",
    "GetInventorySkeleton",
    "GetFolderContent",
    "GetItem",
    "inventory_owner_forbidden",
    "permissions = new",
    "asset_id = item.AssetID.ToString()",
):
    assert marker in api, f"missing inventory API marker: {marker}"

assert 'public const string InventoryRead = "inventory:read"' in security
assert 'public const string InventoryWrite = "inventory:write"' in security
assert '"/api/v1/inventory"' in connector
assert "NexInventoryApi" in connector
assert 'LoadOptionalService<IAssetService>(config, "AssetService")' in connector
assert "new NexInventoryApi(" in connector

for schema in (
    "InventoryPermissions",
    "InventoryFolder",
    "InventoryItem",
    "InventoryTreeResponse",
    "InventoryFolderContentResponse",
    "InventoryItemResponse",
    "InventorySearchScan",
    "InventorySearchResponse",
    "InventoryItemCreateRequest",
    "InventoryItemCreateResponse",
    "InventoryLinkCreateRequest",
    "InventoryLinkCreateResponse",
    "InventoryItemCopyRequest",
    "InventoryItemCopyResponse",
    "InventoryRestoreRequest",
    "InventoryFolderRestoreResponse",
    "InventoryItemRestoreResponse",
):
    assert f'["{schema}"]' in world_api, f"missing Inventory schema: {schema}"

for marker in (
    '["/api/v1/inventory/tree"]',
    '["/api/v1/inventory/search"]',
    '["/api/v1/inventory/folders"]',
    '["/api/v1/inventory/folders/{folderId}"]',
    '["/api/v1/inventory/items"]',
    '["/api/v1/inventory/items/{itemId}"]',
    '["/api/v1/inventory/items/copy"]',
    '["/api/v1/inventory/folders/{folderId}/restore"]',
    '["/api/v1/inventory/items/{itemId}/restore"]',
    '["/api/v1/inventory/links"]',
    '["/api/v1/inventory/lost-and-found"]',
    '["/api/v1/inventory/trash/empty"]',
):
    assert marker in world_api, f"missing Inventory OpenAPI marker: {marker}"

# Firestorm Outfit Gallery / Current Outfit repairs are opt-in and
# restricted to the same authenticated inventory owner as other operations.
native = Path("OpenSim/Services/InventoryService/XInventoryService.cs").read_text()
suitcase = Path("OpenSim/Services/HypergridService/HGSuitcaseInventoryService.cs").read_text()
for source in (native, suitcase):
    assert 'FolderType.MyOutfits' in source, "My Outfits system folder missing"
    assert 'FolderType.CurrentOutfit' in source
    assert '"My Outfits"' in source
    assert 'Array.Exists(sysFolders, f => f.type == (int)FolderType.MyOutfits)' in source
for symbol in (
    '"/api/v1/inventory/outfits/health"',
    '"/api/v1/inventory/outfits/ensure-folders"',
    "private void OutfitHealth(",
    "private void EnsureOutfitFolders(",
    "FolderType.MyOutfits",
    "FolderType.CurrentOutfit",
    "current_outfit_broken_links",
    "no_saved_outfits",
    "current_outfit_link_scan",
    "m_Inventory.GetFolderContent(owner, current.ID)",
    "m_Inventory.GetItem(owner, link.AssetID)",
    "m_Inventory.GetFolder(owner, link.AssetID)",
    "m_Inventory.AddFolder(folder)",
    "outfit_system_folder_protected",
    "broken_link_ids",
    "changed = false",
    "thumbnail_id = folder.ThumbnailID.ToString()",
    "thumbnail_id = item.ThumbnailID.ToString()",
):
    assert symbol in api, f"missing Outfit Gallery repair/health marker: {symbol}"
for symbol in (
    '["/api/v1/inventory/outfits/health"]',
    '["/api/v1/inventory/outfits/ensure-folders"]',
    '["InventoryOutfitHealthResponse"]',
    '["InventoryOutfitEnsureResponse"]',
):
    assert symbol in world_api, f"missing Outfit health API contract: {symbol}"
print("Inventory API contract: OK")
