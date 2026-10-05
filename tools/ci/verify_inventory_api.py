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

print("Inventory API contract: OK")
