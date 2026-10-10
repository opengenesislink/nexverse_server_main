// SPDX-License-Identifier: MPL-2.0
using System;
using System.IO;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.InventoryService;

internal static class Program
{
    static void Assert(bool ok, string msg)
    {
        if (!ok) throw new InvalidOperationException(msg);
    }

    public static int Main()
    {
        MainConsole.Instance = new MockConsole();
        string dir = Path.Combine(Path.GetTempPath(), "ogl-outfit-regression-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "outfits.db");
        try
        {
            var cfg = new IniConfigSource();
            var db = cfg.AddConfig("DatabaseService");
            // XInventoryService uses Assembly.LoadFrom, resolved against the
            // process working directory rather than the .NET dependency
            // probe directory. CI starts this runner from repository root.
            string provider = Path.GetFullPath(
                Path.Combine("bin", "OpenSim.Data.SQLite.dll"));
            Assert(File.Exists(provider), "SQLite storage provider assembly missing");
            db.Set("StorageProvider", provider);
            db.Set("ConnectionString", "URI=file:" + path +
                ",version=3,UseUTF16Encoding=True");
            var service = new XInventoryService(cfg);
            UUID owner = UUID.Random();

            Assert(service.CreateUserInventory(owner), "initial owner inventory creation failed");
            var current = service.GetFolderForType(owner, FolderType.CurrentOutfit);
            var gallery = service.GetFolderForType(owner, FolderType.MyOutfits);
            Assert(current != null && current.Owner == owner, "missing Current Outfit");
            Assert(gallery != null && gallery.Owner == owner, "missing My Outfits");
            Assert(current.ID != gallery.ID, "distinct outfit system folders required");
            Assert(gallery.ParentID == service.GetRootFolder(owner).ID,
                "My Outfits folder not rooted under inventory");

            // A saved outfit remains intact when system-folder setup is
            // requested again. Existing inventory content is never reset.
            var saved = new InventoryFolderBase(UUID.Random(), "My Saved Outfit",
                owner, (short)FolderType.Outfit, gallery.ID, 1);
            Assert(service.AddFolder(saved), "could not add saved outfit folder");
            int before = service.GetInventorySkeleton(owner).Count;
            Assert(!service.CreateUserInventory(owner), "second creation should be idempotent");
            Assert(service.GetInventorySkeleton(owner).Count == before,
                "duplicate system/outfit folder added on second create");
            int textureFolders = service.GetInventorySkeleton(owner)
                .Count(f => f.ParentID == service.GetRootFolder(owner).ID &&
                    f.Type == (short)FolderType.Texture);
            Assert(textureFolders == 1,
                "Texture system folder (type=0) duplicated on repeat creation");
            Assert(service.GetFolderForType(owner, FolderType.MyOutfits).ID == gallery.ID,
                "gallery root changed on repeat initialization");
            Assert(service.GetFolder(owner, saved.ID) != null,
                "saved outfit was deleted by repeat initialization");
            // Simulate a Firestorm preview upload followed by a legacy
            // viewer packet with NO thumbnail field (UUID.Zero). The old
            // UpdateItem/UpdateFolder implementations silently erased the
            // saved preview, which resurfaced only on the next full login.
            UUID itemPreview = UUID.Random();
            UUID folderPreview = UUID.Random();

            var thumbnailItem = new InventoryItemBase(UUID.Random(), owner)
            {
                AssetID = UUID.Random(),
                AssetType = (int)AssetType.Object,
                InvType = (int)InventoryType.Object,
                CreatorId = owner.ToString(),
                Description = "SQLite relog thumbnail regression",
                Folder = gallery.ID,
                Name = "Thumbnail Relog Fixture"
            };
            Assert(service.AddItem(thumbnailItem), "thumbnail fixture create failed");
            var storedItem = service.GetItem(owner, thumbnailItem.ID);
            Assert(storedItem != null, "fixture item lost");
            storedItem.ThumbnailID = itemPreview;
            Assert(service.UpdateItem(storedItem), "item thumbnail save rejected");
            Assert(service.GetItem(owner, thumbnailItem.ID).ThumbnailID == itemPreview,
                "item thumbnail did not persist in SQLite");

            var legacyItem = (InventoryItemBase)service.GetItem(owner, thumbnailItem.ID).Clone();
            legacyItem.ThumbnailID = UUID.Zero; // absent on legacy viewer wire
            legacyItem.Name = "Renamed after upload";
            Assert(service.UpdateItem(legacyItem), "legacy item update rejected");
            Assert(service.GetItem(owner, thumbnailItem.ID).ThumbnailID == itemPreview,
                "legacy item update erased persistent thumbnail");

            var storedFolder = service.GetFolder(owner, saved.ID);
            ushort galleryVersionBeforeThumbnail = service.GetFolder(owner, gallery.ID).Version;
            storedFolder.ThumbnailID = folderPreview;
            storedFolder.Version++;
            Assert(service.UpdateFolder(storedFolder), "outfit folder preview save rejected");
            Assert(service.GetFolder(owner, saved.ID).ThumbnailID == folderPreview,
                "outfit folder thumbnail did not persist");
            Assert(service.GetFolder(owner, gallery.ID).Version >
                galleryVersionBeforeThumbnail,
                "outfit preview update did not invalidate its parent folder version");

            // Updating an existing thumbnail must invalidate both folder and
            // parent versions. This does not simulate Firestorm's private
            // cache, but protects the version contract that the viewer uses.
            ushort savedVersionBeforeReplacement =
                service.GetFolder(owner, saved.ID).Version;
            ushort galleryVersionBeforeReplacement =
                service.GetFolder(owner, gallery.ID).Version;
            UUID folderReplacementPreview = UUID.Random();
            var savedReplacement = service.GetFolder(owner, saved.ID);
            savedReplacement.ThumbnailID = folderReplacementPreview;
            savedReplacement.Version++;
            Assert(service.UpdateFolder(savedReplacement),
                "outfit thumbnail replacement save rejected");
            Assert(service.GetFolder(owner, saved.ID).ThumbnailID ==
                folderReplacementPreview,
                "replacing outfit thumbnail did not persist new UUID");
            Assert(service.GetFolder(owner, saved.ID).Version >
                savedVersionBeforeReplacement,
                "replacing outfit thumbnail did not increment folder version");
            Assert(service.GetFolder(owner, gallery.ID).Version >
                galleryVersionBeforeReplacement,
                "replacing outfit thumbnail did not invalidate parent version");

            // Ordinary folders follow a different XInventoryService update branch
            // from the protected My Outfits system folder and saved outfits.
            var ordinaryFolder = new InventoryFolderBase(UUID.Random(),
                "Normal inventory folder", owner, (short)FolderType.None,
                service.GetRootFolder(owner).ID, 1);
            Assert(service.AddFolder(ordinaryFolder),
                "ordinary thumbnail folder creation failed");
            UUID ordinaryPreview = UUID.Random();
            var ordinaryStored = service.GetFolder(owner, ordinaryFolder.ID);
            ordinaryStored.ThumbnailID = ordinaryPreview;
            ordinaryStored.Version++;
            ushort rootVersionBeforeThumbnail = service.GetRootFolder(owner).Version;
            Assert(service.UpdateFolder(ordinaryStored),
                "ordinary folder thumbnail save rejected");
            Assert(service.GetRootFolder(owner).Version > rootVersionBeforeThumbnail,
                "ordinary preview update did not invalidate inventory root version");
            var ordinaryLegacy = service.GetFolder(owner, ordinaryFolder.ID);
            ordinaryLegacy.ThumbnailID = UUID.Zero;
            ordinaryLegacy.Version++;
            ordinaryLegacy.Name = "Renamed normal folder";
            Assert(service.UpdateFolder(ordinaryLegacy),
                "ordinary legacy folder update rejected");
            Assert(service.GetFolder(owner, ordinaryFolder.ID).ThumbnailID ==
                ordinaryPreview,
                "ordinary legacy update erased folder thumbnail");

            var legacyFolder = service.GetFolder(owner, saved.ID);
            legacyFolder.ThumbnailID = UUID.Zero; // absent on legacy folder wire
            legacyFolder.Version++;
            Assert(service.UpdateFolder(legacyFolder), "legacy folder update rejected");
            Assert(service.GetFolder(owner, saved.ID).ThumbnailID ==
                folderReplacementPreview,
                "legacy folder update erased replacement preview");

            // Also cover system folders (different version-only update path).
            var systemFolder = service.GetFolder(owner, gallery.ID);
            systemFolder.ThumbnailID = folderPreview;
            systemFolder.Version++;
            Assert(service.UpdateFolder(systemFolder), "My Outfits preview save rejected");
            var legacySystem = service.GetFolder(owner, gallery.ID);
            legacySystem.ThumbnailID = UUID.Zero;
            legacySystem.Version++;
            Assert(service.UpdateFolder(legacySystem), "legacy My Outfits update rejected");
            Assert(service.GetFolder(owner, gallery.ID).ThumbnailID == folderPreview,
                "legacy system-folder update erased preview");

            // A fresh inventory service represents the next viewer login:
            // it must load all three thumbnail associations from DISK.
            var fresh = new XInventoryService(cfg);
            Assert(fresh.GetItem(owner, thumbnailItem.ID).ThumbnailID == itemPreview,
                "relogin lost item thumbnail");
            Assert(fresh.GetFolder(owner, saved.ID).ThumbnailID ==
                folderReplacementPreview,
                "relogin lost replaced saved outfit thumbnail");
            Assert(fresh.GetFolder(owner, gallery.ID).ThumbnailID == folderPreview,
                "relogin lost My Outfits thumbnail");
            Assert(fresh.GetFolder(owner, ordinaryFolder.ID).ThumbnailID ==
                ordinaryPreview, "relogin lost ordinary folder thumbnail");
            var freshChildren = fresh.GetFolderContent(owner, gallery.ID);
            Assert(freshChildren.Folders.Any(f =>
                f.ID == saved.ID && f.ThumbnailID == folderReplacementPreview),
                "relogin parent fetch lost replaced outfit child thumbnail");
            var freshRootChildren = fresh.GetFolderContent(
                owner, service.GetRootFolder(owner).ID);
            Assert(freshRootChildren.Folders.Any(f =>
                f.ID == ordinaryFolder.ID && f.ThumbnailID == ordinaryPreview),
                "relogin parent fetch lost ordinary child thumbnail");

            var fetch = new OpenSim.Capabilities.Handlers.FetchInventory2Handler(
                fresh, owner);
            string itemRequest = "<llsd><map><key>items</key><array><map><key>item_id</key><uuid>" +
                thumbnailItem.ID + "</uuid></map></array></map></llsd>";
            string fetchedXml = fetch.FetchInventoryRequest(
                itemRequest, "/FETCH", string.Empty, null, null);
            Assert(fetchedXml.Contains("<key>thumbnail</key>") &&
                fetchedXml.Contains(itemPreview.ToString()),
                "FetchInventory2 omitted reloaded thumbnail.asset_id");
            Console.WriteLine("Outfit and thumbnail SQLite relog + legacy update: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { /* sqlite may still hold handle at process exit */ }
        }
    }
}
