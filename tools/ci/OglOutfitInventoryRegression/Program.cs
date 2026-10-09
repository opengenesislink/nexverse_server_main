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
            UUID preview = UUID.Random();
            var folder = service.GetFolder(owner, saved.ID);
            folder.ThumbnailID = preview;
            folder.Version++;
            Assert(service.UpdateFolder(folder), "failed to save thumbnail");
            Assert(service.GetFolder(owner, saved.ID).ThumbnailID == preview,
                "folder thumbnail missing");
            var legacy = new InventoryFolderBase(saved.ID, "Renamed Outfit",
                owner, (short)FolderType.Outfit, gallery.ID,
                (ushort)(service.GetFolder(owner, saved.ID).Version + 1));
            Assert(service.UpdateFolder(legacy), "legacy folder update failed");
            Assert(service.GetFolder(owner, saved.ID).ThumbnailID == preview,
                "legacy update removed thumbnail");
            var fresh = new XInventoryService(cfg);
            Assert(fresh.GetFolder(owner, saved.ID).ThumbnailID == preview,
                "thumbnail not present after service reconnect");
            var shirt = new InventoryItemBase(UUID.Random(), owner)
            {
                Name = "Thumbnail Regression Shirt",
                AssetID = UUID.Random(),
                Folder = saved.ID,
                AssetType = (int)AssetType.Clothing,
                InvType = (int)InventoryType.Wearable,
                CreatorId = owner.ToString()
            };
            Assert(service.AddItem(shirt), "failed to create regression item");
            UUID itemPreview = UUID.Random();
            var uploaded = service.GetItem(owner, shirt.ID);
            Assert(uploaded != null, "item fetch failed");
            uploaded.ThumbnailID = itemPreview;
            Assert(service.UpdateItem(uploaded), "failed to save item thumbnail");
            var legacyItem = service.GetItem(owner, shirt.ID);
            legacyItem.Name = "Renamed Thumbnail Shirt";
            legacyItem.ThumbnailID = UUID.Zero;
            Assert(service.UpdateItem(legacyItem), "legacy item edit failed");
            var newSession = new XInventoryService(cfg);
            Assert(newSession.GetItem(owner, shirt.ID).ThumbnailID == itemPreview,
                "item thumbnail did not survive relog-equivalent new service");
            Console.WriteLine("Outfit inventory SQLite runtime regression: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { /* sqlite may still hold handle at process exit */ }
        }
    }
}
