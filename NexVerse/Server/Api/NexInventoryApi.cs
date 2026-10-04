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
        private static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly IInventoryService m_Inventory;
        private readonly NexApiAuthenticator m_Authenticator;

        public NexInventoryApi(IInventoryService inventory, NexApiAuthenticator authenticator)
        {
            m_Inventory = inventory;
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
                HandleRead(response, owner, path);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/v1/inventory/trash/empty", StringComparison.OrdinalIgnoreCase))
            {
                EmptyTrash(response, owner);
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

        private void HandleRead(IOSHttpResponse response, UUID owner, string path)
        {
            if (path.Equals("/api/v1/inventory/search", StringComparison.OrdinalIgnoreCase))
            {
                Search(response, owner, null);
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

        private void Search(IOSHttpResponse response, UUID owner, string ignored)
        {
            // Query/sort are deliberately bounded to the inventory skeleton and
            // direct folder contents to avoid an unbounded database operation.
            List<InventoryFolderBase> folders = m_Inventory.GetInventorySkeleton(owner) ?? new List<InventoryFolderBase>();
            List<InventoryItemBase> items = new List<InventoryItemBase>();
            foreach (InventoryFolderBase folder in folders)
            {
                List<InventoryItemBase> children = m_Inventory.GetFolderItems(owner, folder.ID);
                if (children != null) items.AddRange(children);
            }
            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            WriteJson(response, new { folders = ConvertFolders(folders), items = ConvertItems(items), folder_count = folders.Count, item_count = items.Count }, HttpStatusCode.OK);
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
            return body.TryGetProperty(name, out JsonElement e) && UUID.TryParse(e.GetString(), out value);
        }

        private static bool TryName(JsonElement body, string property, out string name)
        {
            name = body.TryGetProperty(property, out JsonElement e) ? (e.GetString() ?? string.Empty).Trim() : string.Empty;
            return name.Length > 0 && name.Length <= 255;
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
