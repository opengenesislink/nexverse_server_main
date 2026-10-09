// SPDX-License-Identifier: BSD-3-Clause
using System;
using System.IO;
using System.Net;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// Firestorm inventory/outfit thumbnail upload CAPS. The initial LLSD
    /// POST receives an item_id or category_id and returns a short-lived,
    /// single-use uploader. Stage 2 persists the JPEG2000 texture asset.
    /// Unlike avatar bakes, these assets MUST NOT be temporary/local.
    ///
    /// Inventory thumbnail associations need a separate thumbnail metadata
    /// service/schema; without one Firestorm can display the uploaded image
    /// immediately but the association may not survive a fresh inventory fetch.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "InventoryThumbnailUploadModule")]
    public sealed class InventoryThumbnailUploadModule : INonSharedRegionModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(InventoryThumbnailUploadModule));

        private Scene m_Scene;
        private string m_Url = "localhost";
        private int m_MaxBytes = 1048576;

        public string Name => "Firestorm Inventory Thumbnail Upload";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig caps = config.Configs["ClientStack.LindenCaps"];
            if (caps != null)
            {
                m_Url = caps.GetString("Cap_InventoryThumbnailUpload", "localhost").Trim();
                m_MaxBytes = Math.Clamp(caps.GetInt("InventoryThumbnailUploadMaxBytes", 1048576),
                    16384, 4194304);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (scene == null || string.IsNullOrEmpty(m_Url))
                return;
            m_Scene = scene;
            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RegionLoaded(Scene scene) { }
        public void PostInitialise() { }
        public void Close() { }

        public void RemoveRegion(Scene scene)
        {
            if (m_Scene != scene)
                return;
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            m_Scene = null;
        }

        private void RegisterCaps(UUID agent, Caps caps)
        {
            if (m_Scene == null || m_Scene.AssetService == null ||
                m_Scene.InventoryService == null)
                return;
            if (!string.Equals(m_Url, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Administrators may explicitly disable the CAP by leaving
                // the URL empty, or forward it to a separately trusted service.
                caps.RegisterHandler("InventoryThumbnailUpload", m_Url);
                return;
            }

            caps.RegisterSimpleHandler("InventoryThumbnailUpload",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (request, response) => BeginUpload(request, response, agent, caps)));
        }

        private bool Authorized(UUID agent, UUID item, UUID folder)
        {
            Scene scene = m_Scene;
            if (scene == null ||
                !scene.TryGetScenePresence(agent, out ScenePresence sp) ||
                sp == null || sp.IsDeleted || sp.IsChildAgent ||
                sp.ControllingClient == null ||
                ((item == UUID.Zero) == (folder == UUID.Zero)))
                return false;

            if (item != UUID.Zero)
            {
                InventoryItemBase ownItem = scene.InventoryService.GetItem(agent, item);
                return ownItem != null && ownItem.Owner == agent && ownItem.ID == item;
            }

            InventoryFolderBase ownFolder = scene.InventoryService.GetFolder(agent, folder);
            return ownFolder != null && ownFolder.Owner == agent &&
                ownFolder.ID == folder;
        }

        private void BeginUpload(IOSHttpRequest request, IOSHttpResponse response,
            UUID agent, Caps caps)
        {
            response.ContentType = "application/llsd+xml";
            response.AddHeader("Cache-Control", "no-store");
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            try
            {
                using MemoryStream stream = new();
                byte[] chunk = new byte[4096];
                int n;
                while ((n = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (stream.Length + n > 8192)
                    {
                        response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                        return;
                    }
                    stream.Write(chunk, 0, n);
                }
                stream.Position = 0;
                if (OSDParser.DeserializeLLSDXml(stream) is not OSDMap input)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                UUID item = input.TryGetValue("item_id", out OSD vItem)
                    ? vItem.AsUUID() : UUID.Zero;
                UUID folder = input.TryGetValue("category_id", out OSD vFolder)
                    ? vFolder.AsUUID() : UUID.Zero;
                if (!Authorized(agent, item, folder))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                string path = "/" + UUID.Random() + "-INVTHUMB";
                // The generated uploader URI matches the same host/port used
                // by existing OpenSim caps; proxy must pass HTTPS CAPS through.
                string scheme = caps.SSLCaps ? "https://" : "http://";
                string url = scheme + caps.HostName + ":" + caps.Port + path;

                var job = new UploadJob(this, caps, path, agent, item, folder,
                    request.RemoteIPEndPoint?.Address, m_MaxBytes);
                var handler = new SimpleBinaryHandler("POST", path, job.CompleteUpload)
                {
                    MaxDataSize = m_MaxBytes
                };
                caps.HttpListener.AddSimpleStreamHandler(handler);
                job.StartTimeout();

                OSDMap result = new()
                {
                    ["uploader"] = OSD.FromString(url),
                    ["state"] = OSD.FromString("upload")
                };
                response.RawBuffer = Util.UTF8NBGetbytes(
                    OSDParser.SerializeLLSDXmlString(result));
                response.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception e)
            {
                m_Log.Warn("[INVENTORY THUMBNAIL]: request rejected: " + e.Message);
                response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
        }

        internal static bool IsJpeg2000(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12)
                return false;
            // JPEG2000 raw codestream signature (J2C):
            bool codestream = bytes[0] == 0xff && bytes[1] == 0x4f &&
                bytes[2] == 0xff && bytes[3] == 0x51;
            // JPEG2000 JP2 container signature box:
            bool container = bytes[0] == 0 && bytes[1] == 0 &&
                bytes[2] == 0 && bytes[3] == 12 &&
                bytes[4] == 0x6a && bytes[5] == 0x50 &&
                bytes[6] == 0x20 && bytes[7] == 0x20 &&
                bytes[8] == 0x0d && bytes[9] == 0x0a &&
                bytes[10] == 0x87 && bytes[11] == 0x0a;
            return codestream || container;
        }

        private sealed class UploadJob
        {
            private readonly InventoryThumbnailUploadModule m_Owner;
            private readonly Caps m_Caps;
            private readonly string m_Path;
            private readonly UUID m_Agent;
            private readonly UUID m_Item;
            private readonly UUID m_Folder;
            private readonly IPAddress m_Remote;
            private readonly int m_MaxBytes;
            private Timer m_Timer;
            private int m_Consumed;

            public UploadJob(InventoryThumbnailUploadModule owner, Caps caps,
                string path, UUID agent, UUID item, UUID folder, IPAddress remote,
                int maxBytes)
            {
                m_Owner = owner;
                m_Caps = caps;
                m_Path = path;
                m_Agent = agent;
                m_Item = item;
                m_Folder = folder;
                m_Remote = remote;
                m_MaxBytes = maxBytes;
            }

            public void StartTimeout()
            {
                m_Timer = new Timer(_ => Cleanup(), null,
                    TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            }

            private void Cleanup()
            {
                if (Interlocked.Exchange(ref m_Consumed, 1) != 0)
                    return;
                m_Caps.HttpListener.RemoveSimpleStreamHandler(m_Path);
                m_Timer?.Dispose();
            }

            public void CompleteUpload(IOSHttpRequest request,
                IOSHttpResponse response, byte[] content)
            {
                response.ContentType = "application/llsd+xml";
                response.AddHeader("Cache-Control", "no-store");
                if (Interlocked.Exchange(ref m_Consumed, 1) != 0)
                {
                    response.StatusCode = (int)HttpStatusCode.Gone;
                    return;
                }
                m_Caps.HttpListener.RemoveSimpleStreamHandler(m_Path);
                m_Timer?.Dispose();

                if (request.HttpMethod != "POST" ||
                    m_Remote == null ||
                    request.RemoteIPEndPoint?.Address == null ||
                    !m_Remote.Equals(request.RemoteIPEndPoint.Address) ||
                    content == null || content.Length == 0 ||
                    content.Length > m_MaxBytes ||
                    !IsJpeg2000(content) ||
                    !m_Owner.Authorized(m_Agent, m_Item, m_Folder))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                try
                {
                    Scene scene = m_Owner.m_Scene;
                    if (scene?.AssetService == null)
                    {
                        response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        return;
                    }

                    UUID assetId = UUID.Random();
                    AssetBase asset = new(assetId,
                        "Inventory Thumbnail", (sbyte)AssetType.Texture,
                        m_Agent.ToString())
                    {
                        Data = content,
                        Temporary = false,
                        Local = false
                    };
                    string storedId = scene.AssetService.Store(asset);
                    if (!UUID.TryParse(storedId, out UUID persisted) ||
                        persisted == UUID.Zero)
                    {
                        response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        return;
                    }

                    // Persist the item/folder association centrally.
                    // Do not return complete if the asset cannot be attached
                    // to the verified owner's inventory record.
                    bool linked = false;
                    if (m_Item != UUID.Zero)
                    {
                        InventoryItemBase existing = scene.InventoryService.GetItem(
                            m_Agent, m_Item);
                        if (existing != null && existing.Owner == m_Agent &&
                            existing.ID == m_Item)
                        {
                            existing.ThumbnailID = persisted;
                            linked = scene.InventoryService.UpdateItem(existing);
                        }
                    }
                    else if (m_Folder != UUID.Zero)
                    {
                        InventoryFolderBase existing = scene.InventoryService.GetFolder(
                            m_Agent, m_Folder);
                        if (existing != null && existing.Owner == m_Agent &&
                            existing.ID == m_Folder)
                        {
                            existing.ThumbnailID = persisted;
                            existing.Version++;
                            linked = scene.InventoryService.UpdateFolder(existing);
                        }
                    }
                    if (!linked)
                    {
                        response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        return;
                    }

                    OSDMap success = new()
                    {
                        ["state"] = OSD.FromString("complete"),
                        ["new_asset"] = OSD.FromUUID(persisted),
                        ["new_inventory_item"] = OSD.FromUUID(UUID.Zero)
                    };
                    response.RawBuffer = Util.UTF8NBGetbytes(
                        OSDParser.SerializeLLSDXmlString(success));
                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                catch (Exception ex)
                {
                    m_Log.Warn("[INVENTORY THUMBNAIL]: asset storage failed: " +
                        ex.Message);
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                }
            }
        }
    }
}
