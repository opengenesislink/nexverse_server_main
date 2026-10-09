// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexInventoryApi
    {
        private const int SearchQueryLengthLimit = 255;
        private const int SearchFolderScanLimit = 2000;
        private const int SearchItemScanLimit = 10000;
        private static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly IInventoryService m_Inventory;
        private readonly IAssetService m_Assets;
        private readonly NexApiAuthenticator m_Authenticator;

        public NexInventoryApi(
            IInventoryService inventory,
            IAssetService assets,
            NexApiAuthenticator authenticator)
        {
            m_Inventory = inventory;
            m_Assets = assets;
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string method = request?.HttpMethod ?? string.Empty;
            string requiredScope = method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                ? NexScopes.InventoryRead : NexScopes.InventoryWrite;

            if (!m_Authenticator.TryAuthenticate(request, requiredScope, out NexPrincipal principal,
                    out UserAccount account, out int statusCode, out string error))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                WriteError(response, (HttpStatusCode)statusCode, error, $"Authentication or {requiredScope} authorization is required.");
                return;
            }

            if (m_Inventory == null)
            {
                WriteError(response, HttpStatusCode.ServiceUnavailable, "inventory_service_unavailable", "InventoryService is unavailable.");
                return;
            }

            UUID owner = account.PrincipalID;
            string requestedOwner = request.QueryString?["owner_id"];
            if (!string.IsNullOrWhiteSpace(requestedOwner))
            {
                if (!UUID.TryParse(requestedOwner, out owner))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_owner_id", "owner_id must be a UUID.");
                    return;
                }
                if (owner != account.PrincipalID && !principal.HasScope(NexScopes.AdminAll))
                {
                    WriteError(response, HttpStatusCode.Forbidden, "inventory_owner_forbidden", "Accessing another resident inventory requires admin:*.");
                    return;
                }
            }

            string path = (request.UriPath ?? string.Empty).TrimEnd('/');
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/restore", StringComparison.OrdinalIgnoreCase))
            {
                Restore(request, response, owner, path);
                return;
            }
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                HandleRead(request, response, owner, path);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/outfits/ensure-folders", StringComparison.OrdinalIgnoreCase))
            {
                EnsureOutfitFolders(response, owner);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/trash/empty", StringComparison.OrdinalIgnoreCase))
            {
                EmptyTrash(response, owner);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/items", StringComparison.OrdinalIgnoreCase))
            {
                if (!principal.HasScope(NexScopes.AdminAll))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Forbidden,
                        "inventory_item_create_admin_required",
                        "Creating an inventory item from an existing asset requires admin:*.");
                    return;
                }

                CreateItem(request, response, owner);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/links", StringComparison.OrdinalIgnoreCase))
            {
                CreateLink(request, response, owner);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/items/copy", StringComparison.OrdinalIgnoreCase))
            {
                CopyItem(request, response, owner);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/folders", StringComparison.OrdinalIgnoreCase))
            {
                CreateFolder(request, response, owner);
                return;
            }

            const string folderPrefix = "/api/v1/inventory/folders/";
            if (path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase) &&
                UUID.TryParse(path.Substring(folderPrefix.Length), out UUID folderID))
            {
                if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
                    UpdateFolder(request, response, owner, folderID);
                else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    TrashFolder(response, owner, folderID);
                else
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "PATCH or DELETE is required.");
                return;
            }

            const string itemPrefix = "/api/v1/inventory/items/";
            if (path.StartsWith(itemPrefix, StringComparison.OrdinalIgnoreCase) &&
                UUID.TryParse(path.Substring(itemPrefix.Length), out UUID itemID))
            {
                if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
                    UpdateItem(request, response, owner, itemID);
                else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    TrashItem(response, owner, itemID);
                else
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "PATCH or DELETE is required.");
                return;
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown inventory endpoint.");
        }

        private void HandleRead(
            IOSHttpRequest request,
            IOSHttpResponse response,
            UUID owner,
            string path)
        {
            if (path.Equals("/api/v1/inventory/outfits/health", StringComparison.OrdinalIgnoreCase))
            {
                OutfitHealth(response, owner);
                return;
            }

            if (path.Equals("/api/v1/inventory/search", StringComparison.OrdinalIgnoreCase))
            {
                Search(request, response, owner);
                return;
            }

            if (path.Equals("/api/v1/inventory/lost-and-found", StringComparison.OrdinalIgnoreCase))
            {
                InventoryFolderBase lost = m_Inventory.GetFolderForType(owner, FolderType.LostAndFound);
                if (lost == null) { NotFound(response, "lost_and_found_not_found"); return; }
                InventoryCollection lostContent = m_Inventory.GetFolderContent(owner, lost.ID);
                WriteJson(response, new { folder = FolderPayload(lost), folders = lostContent?.Folders == null ? Array.Empty<object>() : ConvertFolders(lostContent.Folders), items = lostContent?.Items == null ? Array.Empty<object>() : ConvertItems(lostContent.Items) }, HttpStatusCode.OK);
                return;
            }

            if (path.Equals("/api/v1/inventory", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/v1/inventory/tree", StringComparison.OrdinalIgnoreCase))
            {
                InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
                List<InventoryFolderBase> skeleton = m_Inventory.GetInventorySkeleton(owner) ?? new List<InventoryFolderBase>();
                WriteJson(response, new { owner_id = owner.ToString(), root = root == null ? null : FolderPayload(root),
                    folders = skeleton.ConvertAll(FolderPayload), folder_count = skeleton.Count }, HttpStatusCode.OK);
                return;
            }

            const string folders = "/api/v1/inventory/folders/";
            if (path.StartsWith(folders, StringComparison.OrdinalIgnoreCase) &&
                UUID.TryParse(path.Substring(folders.Length), out UUID folderID))
            {
                InventoryFolderBase folder = m_Inventory.GetFolder(owner, folderID);
                if (!Owned(folder, owner)) { NotFound(response, "inventory_folder_not_found"); return; }
                InventoryCollection content = m_Inventory.GetFolderContent(owner, folderID);
                WriteJson(response, new { folder = FolderPayload(folder),
                    folders = content?.Folders == null ? Array.Empty<object>() : ConvertFolders(content.Folders),
                    items = content?.Items == null ? Array.Empty<object>() : ConvertItems(content.Items) }, HttpStatusCode.OK);
                return;
            }

            const string items = "/api/v1/inventory/items/";
            if (path.StartsWith(items, StringComparison.OrdinalIgnoreCase) &&
                UUID.TryParse(path.Substring(items.Length), out UUID itemID))
            {
                InventoryItemBase item = m_Inventory.GetItem(owner, itemID);
                if (!Owned(item, owner)) { NotFound(response, "inventory_item_not_found"); return; }
                WriteJson(response, new { item = ItemPayload(item) }, HttpStatusCode.OK);
                return;
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown inventory endpoint.");
        }

        // Outfit gallery diagnostics never create, relink, delete or modify
        // wearables. The authenticated inventory owner (or explicit admin:*)
        // is already resolved by Handle() before these methods are called.
        private void OutfitHealth(IOSHttpResponse response, UUID owner)
        {
            InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
            if (!Owned(root, owner))
            {
                WriteError(response, HttpStatusCode.NotFound,
                    "inventory_root_missing", "This resident has no inventory root.");
                return;
            }

            InventoryFolderBase current = m_Inventory.GetFolderForType(owner, FolderType.CurrentOutfit);
            InventoryFolderBase myOutfits = m_Inventory.GetFolderForType(owner, FolderType.MyOutfits);
            InventoryCollection currentContent = Owned(current, owner)
                ? m_Inventory.GetFolderContent(owner, current.ID) : null;
            InventoryCollection galleryContent = Owned(myOutfits, owner)
                ? m_Inventory.GetFolderContent(owner, myOutfits.ID) : null;

            const int inspectionLimit = 256;
            int inspectedLinks = 0;
            int brokenLinks = 0;
            List<string> brokenLinkIds = new();
            ICollection<InventoryItemBase> worn = currentContent?.Items;
            if (worn != null)
            {
                foreach (InventoryItemBase link in worn)
                {
                    if (inspectedLinks >= inspectionLimit)
                        break;
                    inspectedLinks++;
                    if (link == null || link.Owner != owner)
                        continue;
                    if (link.AssetType != (int)AssetType.Link &&
                        link.AssetType != (int)AssetType.LinkFolder)
                        continue;
                    bool valid = link.AssetID != UUID.Zero &&
                        (link.AssetType == (int)AssetType.Link
                            ? Owned(m_Inventory.GetItem(owner, link.AssetID), owner)
                            : Owned(m_Inventory.GetFolder(owner, link.AssetID), owner));
                    if (!valid)
                    {
                        brokenLinks++;
                        brokenLinkIds.Add(link.ID.ToString());
                    }
                }
            }

            int savedOutfits = 0;
            ICollection<InventoryFolderBase> savedFolders = galleryContent?.Folders;
            if (savedFolders != null)
            {
                foreach (InventoryFolderBase saved in savedFolders)
                {
                    if (Owned(saved, owner) &&
                        saved.Type == (short)FolderType.Outfit)
                        savedOutfits++;
                }
            }

            List<string> findings = new();
            if (!Owned(current, owner))
                findings.Add("current_outfit_folder_missing");
            else if (currentContent == null)
                findings.Add("current_outfit_unavailable");
            if (!Owned(myOutfits, owner))
                findings.Add("my_outfits_folder_missing");
            else if (galleryContent == null)
                findings.Add("my_outfits_unavailable");
            if (brokenLinks != 0)
                findings.Add("current_outfit_broken_links");
            if (savedOutfits == 0 && galleryContent != null)
                findings.Add("no_saved_outfits");

            WriteJson(response, new
            {
                owner_id = owner.ToString(),
                current_outfit_folder = Owned(current, owner) ? FolderPayload(current) : null,
                my_outfits_folder = Owned(myOutfits, owner) ? FolderPayload(myOutfits) : null,
                current_outfit_item_count = worn?.Count ?? 0,
                current_outfit_link_scan = new
                {
                    inspected = inspectedLinks,
                    limit = inspectionLimit,
                    truncated = worn != null && worn.Count > inspectionLimit,
                    broken = brokenLinks,
                    broken_link_ids = brokenLinkIds
                },
                saved_outfit_count = savedOutfits,
                findings,
                changed = false
            }, HttpStatusCode.OK);
        }

        private void EnsureOutfitFolders(IOSHttpResponse response, UUID owner)
        {
            InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
            if (!Owned(root, owner))
            {
                WriteError(response, HttpStatusCode.NotFound, "inventory_root_missing",
                    "Cannot create My Outfits without a resident-owned inventory root.");
                return;
            }

            // Deliberately do not touch Current Outfit, wearables, saved outfit
            // links or any existing folder. This is an opt-in additive repair.
            InventoryFolderBase existing =
                m_Inventory.GetFolderForType(owner, FolderType.MyOutfits);
            if (Owned(existing, owner))
            {
                WriteJson(response, new
                {
                    owner_id = owner.ToString(), created = false,
                    my_outfits_folder = FolderPayload(existing)
                }, HttpStatusCode.OK);
                return;
            }

            InventoryFolderBase folder = new(
                UUID.Random(), "My Outfits", owner, (short)FolderType.MyOutfits,
                root.ID, 1);
            if (!m_Inventory.AddFolder(folder))
            {
                // A concurrent request may have already created the folder.
                existing = m_Inventory.GetFolderForType(owner, FolderType.MyOutfits);
                if (!Owned(existing, owner))
                {
                    WriteError(response, HttpStatusCode.Conflict, "outfit_folder_create_failed",
                        "Could not create the missing My Outfits system folder.");
                    return;
                }
                WriteJson(response, new
                {
                    owner_id = owner.ToString(), created = false,
                    my_outfits_folder = FolderPayload(existing)
                }, HttpStatusCode.OK);
                return;
            }
            WriteJson(response, new
            {
                owner_id = owner.ToString(), created = true,
                my_outfits_folder = FolderPayload(folder)
            }, HttpStatusCode.Created);
        }

        private void Search(
            IOSHttpRequest request,
            IOSHttpResponse response,
            UUID owner)
        {
            string query =
                (request?.QueryString?["q"] ?? string.Empty)
                    .Trim();

            if (query.Length > SearchQueryLengthLimit)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "query_too_long",
                    "q must contain at most " +
                    SearchQueryLengthLimit +
                    " characters.");
                return;
            }

            int limit = 100;
            string rawLimit =
                request?.QueryString?["limit"];
            if (!string.IsNullOrWhiteSpace(rawLimit) &&
                (!int.TryParse(rawLimit, out limit) ||
                 limit < 1 ||
                 limit > 500))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_limit",
                    "limit must be between 1 and 500.");
                return;
            }

            List<InventoryFolderBase> allFolders =
                m_Inventory.GetInventorySkeleton(owner) ??
                new List<InventoryFolderBase>();

            InventoryFolderBase root =
                m_Inventory.GetRootFolder(owner);

            if (Owned(root, owner) &&
                !allFolders.Any(folder =>
                    folder.ID == root.ID))
            {
                allFolders.Add(root);
            }

            int availableFolderCount =
                allFolders.Count;

            List<InventoryFolderBase> folders =
                allFolders
                    .Where(folder =>
                        Owned(folder, owner))
                    .GroupBy(folder =>
                        folder.ID)
                    .Select(group =>
                        group.First())
                    .OrderBy(folder =>
                        folder.ID.ToString(),
                        StringComparer.Ordinal)
                    .Take(SearchFolderScanLimit)
                    .ToList();

            bool scanTruncated =
                availableFolderCount >
                folders.Count;

            List<InventoryItemBase> items =
                new List<InventoryItemBase>();

            int scannedFolders = 0;
            foreach (InventoryFolderBase folder in folders)
            {
                if (items.Count >= SearchItemScanLimit)
                {
                    scanTruncated = true;
                    break;
                }

                scannedFolders++;

                List<InventoryItemBase> children =
                    m_Inventory.GetFolderItems(
                        owner,
                        folder.ID);

                if (children == null ||
                    children.Count == 0)
                {
                    continue;
                }

                int remaining =
                    SearchItemScanLimit -
                    items.Count;

                if (children.Count > remaining)
                    scanTruncated = true;

                items.AddRange(
                    children.Take(remaining));
            }

            bool Matches(string value)
            {
                return
                    string.IsNullOrWhiteSpace(query) ||
                    (!string.IsNullOrWhiteSpace(value) &&
                     value.IndexOf(
                         query,
                         StringComparison.OrdinalIgnoreCase) >= 0);
            }

            InventoryFolderBase[] matchedFolders =
                folders
                    .Where(folder =>
                        Matches(folder.Name))
                    .OrderBy(folder =>
                        folder.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(folder =>
                        folder.ID.ToString(),
                        StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray();

            InventoryItemBase[] matchedItems =
                items
                    .Where(item =>
                        Owned(item, owner) &&
                        (Matches(item.Name) ||
                         Matches(item.Description)))
                    .GroupBy(item => item.ID)
                    .Select(group => group.First())
                    .OrderBy(item =>
                        item.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item =>
                        item.ID.ToString(),
                        StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray();

            WriteJson(
                response,
                new
                {
                    query,
                    limit,
                    scan = new
                    {
                        folder_limit =
                            SearchFolderScanLimit,
                        item_limit =
                            SearchItemScanLimit,
                        scanned_folders =
                            scannedFolders,
                        scanned_items =
                            items.Count,
                        truncated =
                            scanTruncated
                    },
                    folders =
                        ConvertFolders(matchedFolders),
                    items =
                        ConvertItems(matchedItems),
                    folder_count =
                        matchedFolders.Length,
                    item_count =
                        matchedItems.Length
                },
                HttpStatusCode.OK);
        }

        private void CreateItem(
            IOSHttpRequest request,
            IOSHttpResponse response,
            UUID owner)
        {
            if (m_Assets == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "asset_service_unavailable",
                    "AssetService is required for inventory item creation.");
                return;
            }

            if (!TryBody(
                    request,
                    response,
                    out JsonElement body) ||
                !TryUuid(
                    body,
                    "folder_id",
                    out UUID folderID) ||
                !TryUuid(
                    body,
                    "asset_id",
                    out UUID assetID) ||
                !TryName(
                    body,
                    "name",
                    out string name))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_item_create_request",
                    "folder_id, asset_id and a non-empty name are required.");
                return;
            }

            InventoryFolderBase folder =
                m_Inventory.GetFolder(
                    owner,
                    folderID);

            if (!Owned(folder, owner))
            {
                NotFound(
                    response,
                    "inventory_target_folder_not_found");
                return;
            }

            AssetMetadata metadata;
            try
            {
                metadata =
                    m_Assets.GetMetadata(
                        assetID.ToString());
            }
            catch (Exception e)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "asset_lookup_failed",
                    "Asset metadata lookup failed: " +
                    e.Message);
                return;
            }

            if (metadata == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "asset_not_found",
                    "The referenced asset was not found.");
                return;
            }

            if (metadata.Type == (sbyte)AssetType.Link ||
                metadata.Type == (sbyte)AssetType.LinkFolder ||
                metadata.Type == (sbyte)AssetType.Folder)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_asset_type",
                    "Use /api/v1/inventory/links for inventory links and folders.");
                return;
            }

            int inventoryType =
                DefaultInventoryTypeForAsset(
                    metadata.Type);

            if (body.TryGetProperty(
                    "inventory_type",
                    out JsonElement invTypeElement))
            {
                if (invTypeElement.ValueKind !=
                        JsonValueKind.Number ||
                    !invTypeElement.TryGetInt32(
                        out inventoryType))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_inventory_type",
                        "inventory_type must be an integer.");
                    return;
                }
            }

            if (!IsInventoryTypeCompatible(
                    metadata.Type,
                    inventoryType))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_inventory_type",
                    "inventory_type is not compatible with the authoritative asset type.");
                return;
            }

            if (!TryPermissionMask(
                    body,
                    "base_permissions",
                    out uint basePermissions) ||
                !TryPermissionMask(
                    body,
                    "current_permissions",
                    out uint currentPermissions) ||
                !TryPermissionMask(
                    body,
                    "next_owner_permissions",
                    out uint nextPermissions))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "permission_masks_required",
                    "base_permissions, current_permissions and next_owner_permissions are required uint values.");
                return;
            }

            if (!TryOptionalPermissionMask(
                    body,
                    "everyone_permissions",
                    0,
                    out uint everyonePermissions) ||
                !TryOptionalPermissionMask(
                    body,
                    "group_permissions",
                    0,
                    out uint groupPermissions) ||
                !TryOptionalPermissionMask(
                    body,
                    "flags",
                    0,
                    out uint flags))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_optional_mask",
                    "everyone_permissions, group_permissions and flags must be uint values when supplied.");
                return;
            }

            string creatorId =
                UUID.TryParse(
                    metadata.CreatorID,
                    out UUID creator) &&
                creator != UUID.Zero
                    ? creator.ToString()
                    : owner.ToString();

            InventoryItemBase item =
                new InventoryItemBase(
                    UUID.Random(),
                    owner)
                {
                    AssetID = assetID,
                    AssetType = metadata.Type,
                    InvType = inventoryType,
                    Folder = folderID,
                    Name = name,
                    Description =
                        body.TryGetProperty(
                            "description",
                            out JsonElement description) &&
                        description.ValueKind ==
                            JsonValueKind.String
                            ? description.GetString() ??
                              string.Empty
                            : metadata.Description ??
                              string.Empty,
                    CreatorId = creatorId,
                    BasePermissions = basePermissions,
                    CurrentPermissions =
                        currentPermissions &
                        basePermissions,
                    NextPermissions =
                        nextPermissions &
                        basePermissions,
                    EveryOnePermissions =
                        everyonePermissions &
                        basePermissions,
                    GroupPermissions =
                        groupPermissions &
                        basePermissions,
                    Flags = flags
                };

            if (!m_Inventory.AddItem(item))
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "inventory_item_create_failed",
                    "Inventory item could not be created.");
                return;
            }

            WriteJson(
                response,
                new
                {
                    item =
                        ItemPayload(item),
                    asset = new
                    {
                        id =
                            metadata.ID,
                        type =
                            metadata.Type,
                        content_type =
                            metadata.ContentType
                    }
                },
                HttpStatusCode.Created);
        }

        private void CreateLink(
            IOSHttpRequest request,
            IOSHttpResponse response,
            UUID owner)
        {
            if (!TryBody(
                    request,
                    response,
                    out JsonElement body) ||
                !TryUuid(
                    body,
                    "folder_id",
                    out UUID folderID) ||
                !TryUuid(
                    body,
                    "target_id",
                    out UUID targetID))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_link_request",
                    "folder_id and target_id are required.");
                return;
            }

            string targetKind =
                body.TryGetProperty(
                    "target_kind",
                    out JsonElement kindElement) &&
                kindElement.ValueKind ==
                    JsonValueKind.String
                    ? (kindElement.GetString() ??
                       string.Empty)
                        .Trim()
                        .ToLowerInvariant()
                    : "item";

            InventoryFolderBase destination =
                m_Inventory.GetFolder(
                    owner,
                    folderID);

            if (!Owned(destination, owner))
            {
                NotFound(
                    response,
                    "inventory_target_folder_not_found");
                return;
            }

            string name;
            string description;
            int invType;
            uint flags;
            int linkAssetType;

            if (targetKind == "item")
            {
                InventoryItemBase target =
                    m_Inventory.GetItem(
                        owner,
                        targetID);

                if (!Owned(target, owner))
                {
                    NotFound(
                        response,
                        "inventory_link_target_not_found");
                    return;
                }

                if (target.AssetType ==
                        (int)AssetType.Link ||
                    target.AssetType ==
                        (int)AssetType.LinkFolder)
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "inventory_link_chain_forbidden",
                        "Link targets must be direct inventory items or folders.");
                    return;
                }

                name =
                    OptionalName(
                        body,
                        "name",
                        target.Name);
                description =
                    OptionalString(
                        body,
                        "description",
                        target.Description);
                invType =
                    target.InvType;
                flags =
                    target.Flags;
                linkAssetType =
                    (int)AssetType.Link;
            }
            else if (targetKind == "folder")
            {
                InventoryFolderBase target =
                    m_Inventory.GetFolder(
                        owner,
                        targetID);

                if (!Owned(target, owner))
                {
                    NotFound(
                        response,
                        "inventory_link_target_not_found");
                    return;
                }

                name =
                    OptionalName(
                        body,
                        "name",
                        target.Name);
                description =
                    OptionalString(
                        body,
                        "description",
                        string.Empty);
                invType =
                    (int)InventoryType.Unknown;
                flags = 0;
                linkAssetType =
                    (int)AssetType.LinkFolder;
            }
            else
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_target_kind",
                    "target_kind must be item or folder.");
                return;
            }

            InventoryItemBase link =
                new InventoryItemBase(
                    UUID.Random(),
                    owner)
                {
                    AssetID = targetID,
                    AssetType = linkAssetType,
                    CreatorId =
                        owner.ToString(),
                    InvType = invType,
                    Description = description,
                    Folder = folderID,
                    Flags = flags,
                    Name = name,
                    BasePermissions =
                        (uint)OpenSim.Framework.PermissionMask.Copy,
                    CurrentPermissions =
                        (uint)OpenSim.Framework.PermissionMask.Copy,
                    EveryOnePermissions =
                        (uint)OpenSim.Framework.PermissionMask.Copy,
                    GroupPermissions =
                        (uint)OpenSim.Framework.PermissionMask.Copy,
                    NextPermissions =
                        (uint)OpenSim.Framework.PermissionMask.Copy
                };

            if (!m_Inventory.AddItem(link))
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "inventory_link_create_failed",
                    "Inventory link could not be created.");
                return;
            }

            WriteJson(
                response,
                new
                {
                    link =
                        ItemPayload(link),
                    target_kind =
                        targetKind,
                    target_id =
                        targetID.ToString()
                },
                HttpStatusCode.Created);
        }

        private void CopyItem(IOSHttpRequest request, IOSHttpResponse response, UUID owner)
        {
            if (!TryBody(request, response, out JsonElement body) ||
                !TryUuid(body, "item_id", out UUID itemID) ||
                !TryUuid(body, "folder_id", out UUID folderID))
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_copy_request", "item_id and folder_id are required.");
                return;
            }
            InventoryItemBase source = m_Inventory.GetItem(owner, itemID);
            InventoryFolderBase target = m_Inventory.GetFolder(owner, folderID);
            if (!Owned(source, owner) || !Owned(target, owner)) { NotFound(response, "inventory_copy_source_or_target_not_found"); return; }
            if ((source.CurrentPermissions & (uint)OpenSim.Framework.PermissionMask.Copy) == 0)
            {
                WriteError(
                    response,
                    HttpStatusCode.Forbidden,
                    "inventory_item_copy_forbidden",
                    "The inventory item does not grant Copy permission.");
                return;
            }

            InventoryItemBase copy = (InventoryItemBase)source.Clone();
            copy.ID = UUID.Random();
            copy.Folder = folderID;
            if (body.TryGetProperty("name", out JsonElement n) && !string.IsNullOrWhiteSpace(n.GetString()))
                copy.Name = n.GetString().Trim();
            if (!m_Inventory.AddItem(copy)) { WriteError(response, HttpStatusCode.Conflict, "inventory_item_copy_failed", "Item could not be copied."); return; }
            WriteJson(response, new { item = ItemPayload(copy), copied_from = source.ID.ToString() }, HttpStatusCode.Created);
        }

        private void EmptyTrash(IOSHttpResponse response, UUID owner)
        {
            InventoryFolderBase trash = m_Inventory.GetFolderForType(owner, FolderType.Trash);
            if (trash == null) { WriteError(response, HttpStatusCode.Conflict, "trash_unavailable", "Trash folder is unavailable."); return; }
            if (!m_Inventory.PurgeFolder(trash)) { WriteError(response, HttpStatusCode.Conflict, "trash_purge_failed", "Trash could not be emptied."); return; }
            WriteJson(response, new { emptied = true, trash_id = trash.ID.ToString() }, HttpStatusCode.OK);
        }

        private void Restore(IOSHttpRequest request, IOSHttpResponse response, UUID owner, string path)
        {
            if (!TryBody(request, response, out JsonElement body) || !TryUuid(body, "folder_id", out UUID targetID))
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_restore_request", "folder_id is required as restore target.");
                return;
            }
            InventoryFolderBase target = m_Inventory.GetFolder(owner, targetID);
            InventoryFolderBase trash = m_Inventory.GetFolderForType(owner, FolderType.Trash);
            if (!Owned(target, owner) || trash == null) { NotFound(response, "inventory_restore_target_not_found"); return; }

            const string folderPrefix = "/api/v1/inventory/folders/";
            const string itemPrefix = "/api/v1/inventory/items/";
            if (path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = path.Substring(folderPrefix.Length);
                raw = raw.Substring(0, raw.Length - "/restore".Length);
                if (!UUID.TryParse(raw, out UUID id)) { WriteError(response, HttpStatusCode.BadRequest, "invalid_folder_id", "Folder id must be a UUID."); return; }
                InventoryFolderBase folder = m_Inventory.GetFolder(owner, id);
                if (!Owned(folder, owner) || folder.ParentID != trash.ID) { WriteError(response, HttpStatusCode.Conflict, "folder_not_in_trash", "Folder is not directly in Trash."); return; }
                if (WouldCreateFolderCycle(owner, folder.ID, target.ID))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "inventory_folder_cycle",
                        "Restore target would create or preserve an invalid folder cycle.");
                    return;
                }
                folder.ParentID = target.ID;
                if (!m_Inventory.MoveFolder(folder)) { WriteError(response, HttpStatusCode.Conflict, "inventory_restore_failed", "Folder could not be restored."); return; }
                WriteJson(response, new { restored = true, folder = FolderPayload(folder) }, HttpStatusCode.OK);
                return;
            }
            if (path.StartsWith(itemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = path.Substring(itemPrefix.Length);
                raw = raw.Substring(0, raw.Length - "/restore".Length);
                if (!UUID.TryParse(raw, out UUID id)) { WriteError(response, HttpStatusCode.BadRequest, "invalid_item_id", "Item id must be a UUID."); return; }
                InventoryItemBase item = m_Inventory.GetItem(owner, id);
                if (!Owned(item, owner) || item.Folder != trash.ID) { WriteError(response, HttpStatusCode.Conflict, "item_not_in_trash", "Item is not directly in Trash."); return; }
                item.Folder = target.ID;
                if (!m_Inventory.MoveItems(owner, new List<InventoryItemBase> { item })) { WriteError(response, HttpStatusCode.Conflict, "inventory_restore_failed", "Item could not be restored."); return; }
                WriteJson(response, new { restored = true, item = ItemPayload(item) }, HttpStatusCode.OK);
                return;
            }
            WriteError(response, HttpStatusCode.NotFound, "restore_route_not_found", "Unknown restore endpoint.");
        }

        private void CreateFolder(IOSHttpRequest request, IOSHttpResponse response, UUID owner)
        {
            if (!TryBody(request, response, out JsonElement body)) return;
            if (!TryUuid(body, "parent_id", out UUID parentID) || !TryName(body, "name", out string name)) {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_folder", "parent_id and a non-empty name are required."); return; }
            InventoryFolderBase parent = m_Inventory.GetFolder(owner, parentID);
            if (!Owned(parent, owner)) { NotFound(response, "inventory_parent_not_found"); return; }

            InventoryFolderBase folder = new InventoryFolderBase(UUID.Random(), name, owner, (short)FolderType.None, parentID, 1);
            if (!m_Inventory.AddFolder(folder)) { WriteError(response, HttpStatusCode.Conflict, "inventory_folder_create_failed", "Folder could not be created."); return; }
            WriteJson(response, new { folder = FolderPayload(folder) }, HttpStatusCode.Created);
        }

        private void UpdateFolder(IOSHttpRequest request, IOSHttpResponse response, UUID owner, UUID folderID)
        {
            InventoryFolderBase folder = m_Inventory.GetFolder(owner, folderID);
            if (!Owned(folder, owner)) { NotFound(response, "inventory_folder_not_found"); return; }
            InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
            if (root != null && root.ID == folder.ID) { WriteError(response, HttpStatusCode.Conflict, "root_folder_protected", "The inventory root cannot be renamed or moved."); return; }
            if (!TryBody(request, response, out JsonElement body)) return;

            bool changed = false;
            if (body.TryGetProperty("name", out JsonElement n)) {
                string name = (n.GetString() ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > 255) { WriteError(response, HttpStatusCode.BadRequest, "invalid_name", "Folder name must contain 1-255 characters."); return; }
                folder.Name = name; changed = true;
            }
            if (body.TryGetProperty("parent_id", out JsonElement p)) {
                if (!UUID.TryParse(p.GetString(), out UUID parentID) || parentID == folder.ID) { WriteError(response, HttpStatusCode.BadRequest, "invalid_parent_id", "parent_id must name another folder."); return; }
                InventoryFolderBase parent = m_Inventory.GetFolder(owner, parentID);
                if (!Owned(parent, owner)) { NotFound(response, "inventory_parent_not_found"); return; }
                if (WouldCreateFolderCycle(owner, folder.ID, parentID))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "inventory_folder_cycle",
                        "Moving the folder to that parent would create or preserve an invalid folder cycle.");
                    return;
                }
                folder.ParentID = parentID;
                if (!m_Inventory.MoveFolder(folder)) { WriteError(response, HttpStatusCode.Conflict, "inventory_folder_move_failed", "Folder could not be moved."); return; }
                changed = true;
            }
            if (!changed) { WriteError(response, HttpStatusCode.BadRequest, "no_changes", "name or parent_id is required."); return; }
            if (!m_Inventory.UpdateFolder(folder)) { WriteError(response, HttpStatusCode.Conflict, "inventory_folder_update_failed", "Folder could not be updated."); return; }
            WriteJson(response, new { folder = FolderPayload(folder) }, HttpStatusCode.OK);
        }

        private void TrashFolder(IOSHttpResponse response, UUID owner, UUID folderID)
        {
            InventoryFolderBase folder = m_Inventory.GetFolder(owner, folderID);
            InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
            if (!Owned(folder, owner)) { NotFound(response, "inventory_folder_not_found"); return; }
            if (root != null && root.ID == folder.ID) { WriteError(response, HttpStatusCode.Conflict, "root_folder_protected", "The inventory root cannot be deleted."); return; }
            InventoryFolderBase trash = m_Inventory.GetFolderForType(owner, FolderType.Trash);
            if (trash == null || trash.ID == folder.ID) { WriteError(response, HttpStatusCode.Conflict, "trash_unavailable", "Trash folder is unavailable or protected."); return; }
            if (WouldCreateFolderCycle(owner, folder.ID, trash.ID))
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "inventory_folder_cycle",
                    "Trash destination would create or preserve an invalid folder cycle.");
                return;
            }
            folder.ParentID = trash.ID;
            if (!m_Inventory.MoveFolder(folder)) { WriteError(response, HttpStatusCode.Conflict, "inventory_folder_trash_failed", "Folder could not be moved to Trash."); return; }
            WriteJson(response, new { trashed = true, folder_id = folder.ID.ToString(), trash_id = trash.ID.ToString() }, HttpStatusCode.OK);
        }

        private void UpdateItem(IOSHttpRequest request, IOSHttpResponse response, UUID owner, UUID itemID)
        {
            InventoryItemBase item = m_Inventory.GetItem(owner, itemID);
            if (!Owned(item, owner)) { NotFound(response, "inventory_item_not_found"); return; }
            if (!TryBody(request, response, out JsonElement body)) return;
            bool changed = false;
            if (body.TryGetProperty("name", out JsonElement n)) {
                string name = (n.GetString() ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > 255) { WriteError(response, HttpStatusCode.BadRequest, "invalid_name", "Item name must contain 1-255 characters."); return; }
                item.Name = name; changed = true;
            }
            if (body.TryGetProperty("description", out JsonElement d)) { item.Description = d.GetString() ?? string.Empty; changed = true; }
            if (body.TryGetProperty("folder_id", out JsonElement f)) {
                if (!UUID.TryParse(f.GetString(), out UUID folderID)) { WriteError(response, HttpStatusCode.BadRequest, "invalid_folder_id", "folder_id must be a UUID."); return; }
                InventoryFolderBase target = m_Inventory.GetFolder(owner, folderID);
                if (!Owned(target, owner)) { NotFound(response, "inventory_target_folder_not_found"); return; }
                item.Folder = folderID;
                if (!m_Inventory.MoveItems(owner, new List<InventoryItemBase> { item })) { WriteError(response, HttpStatusCode.Conflict, "inventory_item_move_failed", "Item could not be moved."); return; }
                changed = true;
            }
            if (!changed) { WriteError(response, HttpStatusCode.BadRequest, "no_changes", "name, description or folder_id is required."); return; }
            InventoryItemBase persisted = m_Inventory.GetItem(owner, itemID);
            if (persisted != null) { persisted.Name = item.Name; persisted.Description = item.Description; item = persisted; }
            if (!m_Inventory.UpdateItem(item)) { WriteError(response, HttpStatusCode.Conflict, "inventory_item_update_failed", "Item could not be updated."); return; }
            WriteJson(response, new { item = ItemPayload(item) }, HttpStatusCode.OK);
        }

        private void TrashItem(IOSHttpResponse response, UUID owner, UUID itemID)
        {
            InventoryItemBase item = m_Inventory.GetItem(owner, itemID);
            if (!Owned(item, owner)) { NotFound(response, "inventory_item_not_found"); return; }
            InventoryFolderBase trash = m_Inventory.GetFolderForType(owner, FolderType.Trash);
            if (trash == null) { WriteError(response, HttpStatusCode.Conflict, "trash_unavailable", "Trash folder is unavailable."); return; }
            item.Folder = trash.ID;
            if (!m_Inventory.MoveItems(owner, new List<InventoryItemBase> { item })) { WriteError(response, HttpStatusCode.Conflict, "inventory_item_trash_failed", "Item could not be moved to Trash."); return; }
            WriteJson(response, new { trashed = true, item_id = item.ID.ToString(), trash_id = trash.ID.ToString() }, HttpStatusCode.OK);
        }

        private bool WouldCreateFolderCycle(
            UUID owner,
            UUID movingFolderID,
            UUID targetParentID)
        {
            HashSet<UUID> visited =
                new HashSet<UUID>();
            UUID current =
                targetParentID;

            for (int depth = 0;
                 depth < 4096 &&
                 current != UUID.Zero;
                 depth++)
            {
                if (current == movingFolderID)
                    return true;

                if (!visited.Add(current))
                    return true;

                InventoryFolderBase currentFolder =
                    m_Inventory.GetFolder(
                        owner,
                        current);

                if (!Owned(currentFolder, owner))
                    return true;

                current =
                    currentFolder.ParentID;
            }

            return
                current != UUID.Zero;
        }

        private static bool Owned(InventoryFolderBase value, UUID owner) => value != null && value.Owner == owner;
        private static bool Owned(InventoryItemBase value, UUID owner) => value != null && value.Owner == owner;

        private static bool TryBody(IOSHttpRequest request, IOSHttpResponse response, out JsonElement body)
        {
            try { using JsonDocument doc = JsonDocument.Parse(request.InputStream); body = doc.RootElement.Clone(); return body.ValueKind == JsonValueKind.Object; }
            catch { body = default; WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "A valid JSON object is required."); return false; }
        }

        private static bool TryUuid(JsonElement body, string name, out UUID value)
        {
            value = UUID.Zero;

            return
                body.TryGetProperty(
                    name,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.String &&
                UUID.TryParse(
                    element.GetString(),
                    out value) &&
                value != UUID.Zero;
        }

        private static bool TryName(JsonElement body, string property, out string name)
        {
            name = string.Empty;

            if (!body.TryGetProperty(
                    property,
                    out JsonElement element) ||
                element.ValueKind !=
                    JsonValueKind.String)
            {
                return false;
            }

            name =
                (element.GetString() ??
                 string.Empty)
                    .Trim();

            return
                name.Length > 0 &&
                name.Length <= 255;
        }

        private static bool TryPermissionMask(
            JsonElement body,
            string name,
            out uint value)
        {
            value = 0;

            return
                body.TryGetProperty(
                    name,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.Number &&
                element.TryGetUInt32(
                    out value);
        }

        private static bool TryOptionalPermissionMask(
            JsonElement body,
            string name,
            uint fallback,
            out uint value)
        {
            value = fallback;

            if (!body.TryGetProperty(
                    name,
                    out JsonElement element))
            {
                return true;
            }

            return
                element.ValueKind ==
                    JsonValueKind.Number &&
                element.TryGetUInt32(
                    out value);
        }

        private static int DefaultInventoryTypeForAsset(
            sbyte assetType)
        {
            return (AssetType)assetType switch
            {
                AssetType.Texture =>
                    (int)InventoryType.Texture,
                AssetType.TextureTGA =>
                    (int)InventoryType.Texture,
                AssetType.ImageTGA =>
                    (int)InventoryType.Texture,
                AssetType.ImageJPEG =>
                    (int)InventoryType.Texture,
                AssetType.Sound =>
                    (int)InventoryType.Sound,
                AssetType.SoundWAV =>
                    (int)InventoryType.Sound,
                AssetType.CallingCard =>
                    (int)InventoryType.CallingCard,
                AssetType.Landmark =>
                    (int)InventoryType.Landmark,
                AssetType.Clothing =>
                    (int)InventoryType.Wearable,
                AssetType.Bodypart =>
                    (int)InventoryType.Wearable,
                AssetType.Object =>
                    (int)InventoryType.Object,
                AssetType.Notecard =>
                    (int)InventoryType.Notecard,
                AssetType.LSLText =>
                    (int)InventoryType.LSL,
                AssetType.LSLBytecode =>
                    (int)InventoryType.LSL,
                AssetType.Animation =>
                    (int)InventoryType.Animation,
                AssetType.Gesture =>
                    (int)InventoryType.Gesture,
                AssetType.Simstate =>
                    (int)InventoryType.Snapshot,
                AssetType.Mesh =>
                    (int)InventoryType.Mesh,
                AssetType.Settings =>
                    (int)InventoryType.Settings,
                AssetType.Material =>
                    (int)InventoryType.Material,
                _ =>
                    (int)InventoryType.Unknown
            };
        }

        private static bool IsInventoryTypeCompatible(
            sbyte assetType,
            int inventoryType)
        {
            AssetType type =
                (AssetType)assetType;

            if (type == AssetType.Object)
            {
                return
                    inventoryType ==
                        (int)InventoryType.Object ||
                    inventoryType ==
                        (int)InventoryType.Attachment;
            }

            if (type == AssetType.Texture ||
                type == AssetType.TextureTGA ||
                type == AssetType.ImageTGA ||
                type == AssetType.ImageJPEG)
            {
                return
                    inventoryType ==
                        (int)InventoryType.Texture ||
                    inventoryType ==
                        (int)InventoryType.Snapshot;
            }

            return
                inventoryType ==
                DefaultInventoryTypeForAsset(
                    assetType);
        }

        private static string OptionalString(
            JsonElement body,
            string name,
            string fallback)
        {
            if (body.TryGetProperty(
                    name,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.String)
            {
                return
                    element.GetString() ??
                    string.Empty;
            }

            return
                fallback ??
                string.Empty;
        }

        private static string OptionalName(
            JsonElement body,
            string name,
            string fallback)
        {
            string value =
                OptionalString(
                    body,
                    name,
                    fallback)
                    .Trim();

            if (value.Length == 0)
                value =
                    string.IsNullOrWhiteSpace(fallback)
                        ? "Link"
                        : fallback.Trim();

            return
                value.Length <= 255
                    ? value
                    : value.Substring(0, 255);
        }

        private static void NotFound(IOSHttpResponse response, string code) =>
            WriteError(response, HttpStatusCode.NotFound, code, "Inventory object was not found.");

        private static object[] ConvertFolders(ICollection<InventoryFolderBase> values) { List<object> r = new(); foreach (var v in values) r.Add(FolderPayload(v)); return r.ToArray(); }
        private static object[] ConvertItems(ICollection<InventoryItemBase> values) { List<object> r = new(); foreach (var v in values) r.Add(ItemPayload(v)); return r.ToArray(); }

        private static object FolderPayload(InventoryFolderBase folder) => new { id = folder.ID.ToString(), owner_id = folder.Owner.ToString(), parent_id = folder.ParentID.ToString(), name = folder.Name, type = folder.Type, version = folder.Version };
        private static object ItemPayload(InventoryItemBase item) => new {
            id = item.ID.ToString(), owner_id = item.Owner.ToString(), folder_id = item.Folder.ToString(), asset_id = item.AssetID.ToString(),
            name = item.Name, description = item.Description, asset_type = item.AssetType, inventory_type = item.InvType,
            creator_id = item.CreatorId, creator_data = item.CreatorData, creation_date = item.CreationDate, flags = item.Flags,
            permissions = new { base_mask = item.BasePermissions, current_mask = item.CurrentPermissions, everyone_mask = item.EveryOnePermissions, group_mask = item.GroupPermissions, next_owner_mask = item.NextPermissions }
        };

        private static void WriteJson(IOSHttpResponse response, object payload, HttpStatusCode status) {
            response.StatusCode = (int)status; response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, s_Json)); }
        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string code, string message) =>
            WriteJson(response, new { error = code, error_description = message }, status);
    }
}
