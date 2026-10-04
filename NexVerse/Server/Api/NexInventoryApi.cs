// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
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
            if (!string.Equals(request?.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "GET is required.");
                return;
            }

            if (!m_Authenticator.TryAuthenticate(request, NexScopes.InventoryRead, out NexPrincipal principal,
                    out UserAccount account, out int statusCode, out string error))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                WriteError(response, (HttpStatusCode)statusCode, error, "Authentication or inventory:read authorization is required.");
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
                    WriteError(response, HttpStatusCode.Forbidden, "inventory_owner_forbidden", "Reading another resident inventory requires admin:*.");
                    return;
                }
            }

            string path = (request.UriPath ?? string.Empty).TrimEnd('/');
            if (path.Equals("/api/v1/inventory", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/v1/inventory/tree", StringComparison.OrdinalIgnoreCase))
            {
                WriteTree(response, owner);
                return;
            }

            const string folders = "/api/v1/inventory/folders/";
            if (path.StartsWith(folders, StringComparison.OrdinalIgnoreCase))
            {
                if (!UUID.TryParse(path.Substring(folders.Length), out UUID folderID))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_folder_id", "Folder id must be a UUID.");
                    return;
                }
                WriteFolder(response, owner, folderID);
                return;
            }

            const string items = "/api/v1/inventory/items/";
            if (path.StartsWith(items, StringComparison.OrdinalIgnoreCase))
            {
                if (!UUID.TryParse(path.Substring(items.Length), out UUID itemID))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_item_id", "Item id must be a UUID.");
                    return;
                }
                WriteItem(response, owner, itemID);
                return;
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown inventory endpoint.");
        }

        private void WriteTree(IOSHttpResponse response, UUID owner)
        {
            InventoryFolderBase root = m_Inventory.GetRootFolder(owner);
            List<InventoryFolderBase> folders = m_Inventory.GetInventorySkeleton(owner) ?? new List<InventoryFolderBase>();
            WriteJson(response, new
            {
                owner_id = owner.ToString(),
                root = root == null ? null : FolderPayload(root),
                folders = folders.ConvertAll(FolderPayload),
                folder_count = folders.Count
            }, HttpStatusCode.OK);
        }

        private void WriteFolder(IOSHttpResponse response, UUID owner, UUID folderID)
        {
            InventoryFolderBase folder = m_Inventory.GetFolder(owner, folderID);
            if (folder == null || folder.Owner != owner)
            {
                WriteError(response, HttpStatusCode.NotFound, "inventory_folder_not_found", "Inventory folder was not found.");
                return;
            }

            InventoryCollection content = m_Inventory.GetFolderContent(owner, folderID);
            WriteJson(response, new
            {
                folder = FolderPayload(folder),
                folders = content?.Folders == null ? Array.Empty<object>() : ConvertFolders(content.Folders),
                items = content?.Items == null ? Array.Empty<object>() : ConvertItems(content.Items)
            }, HttpStatusCode.OK);
        }

        private void WriteItem(IOSHttpResponse response, UUID owner, UUID itemID)
        {
            InventoryItemBase item = m_Inventory.GetItem(owner, itemID);
            if (item == null || item.Owner != owner)
            {
                WriteError(response, HttpStatusCode.NotFound, "inventory_item_not_found", "Inventory item was not found.");
                return;
            }
            WriteJson(response, new { item = ItemPayload(item) }, HttpStatusCode.OK);
        }

        private static object[] ConvertFolders(ICollection<InventoryFolderBase> values)
        {
            List<object> result = new List<object>();
            foreach (InventoryFolderBase value in values) result.Add(FolderPayload(value));
            return result.ToArray();
        }

        private static object[] ConvertItems(ICollection<InventoryItemBase> values)
        {
            List<object> result = new List<object>();
            foreach (InventoryItemBase value in values) result.Add(ItemPayload(value));
            return result.ToArray();
        }

        private static object FolderPayload(InventoryFolderBase folder) => new
        {
            id = folder.ID.ToString(),
            owner_id = folder.Owner.ToString(),
            parent_id = folder.ParentID.ToString(),
            name = folder.Name,
            type = folder.Type,
            version = folder.Version
        };

        private static object ItemPayload(InventoryItemBase item) => new
        {
            id = item.ID.ToString(),
            owner_id = item.Owner.ToString(),
            folder_id = item.Folder.ToString(),
            asset_id = item.AssetID.ToString(),
            name = item.Name,
            description = item.Description,
            asset_type = item.AssetType,
            inventory_type = item.InvType,
            creator_id = item.CreatorId,
            creator_data = item.CreatorData,
            creation_date = item.CreationDate,
            flags = item.Flags,
            permissions = new
            {
                base_mask = item.BasePermissions,
                current_mask = item.CurrentPermissions,
                everyone_mask = item.EveryOnePermissions,
                group_mask = item.GroupPermissions,
                next_owner_mask = item.NextPermissions
            },
            asset_permissions_union = item.AssetID.IsZero() ? 0 : (int?)null
        };

        private static void WriteJson(IOSHttpResponse response, object payload, HttpStatusCode status)
        {
            byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, s_Json));
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = data;
        }

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string code, string message)
        {
            WriteJson(response, new { error = code, error_description = message }, status);
        }
    }
}
