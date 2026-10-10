// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using log4net;
using Mono.Addins;
using NexVerse.Core.Pathfinding;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// An explicitly trusted scene/physics component must supply all of the
    /// following for the same scene revision. The transport MUST contain real
    /// Firestorm/Havok-compatible binary data, not a raster grid, an invented
    /// mesh header or a compressed JSON graph. Metadata maps must follow
    /// Firestorm's RegionObjects, TerrainNavMeshProperties and
    /// CharacterProperties LLSD field contracts.
    /// </summary>
    public interface IOglFirestormNavMeshSource
    {
        bool TryCaptureFirestormSnapshot(Scene scene,
            out OglFirestormNavMeshSnapshot snapshot);
    }

    /// <summary>
    /// Published, scene-revision-consistent read-only Firestorm NavMesh view.
    /// Byte transport is copied/validated by OglFirestormNavMeshTransport.
    /// The metadata is retained as immutable-by-contract snapshots supplied
    /// by the trusted source. Consumers must never mutate these maps.
    /// </summary>
    public sealed class OglFirestormNavMeshSnapshot
    {
        public OglFirestormNavMeshTransport Transport { get; }
        public OSDMap RegionObjects { get; }
        public OSDMap TerrainProperties { get; }
        public OSDMap Characters { get; }

        public OglFirestormNavMeshSnapshot(
            OglFirestormNavMeshTransport transport,
            OSDMap regionObjects, OSDMap terrainProperties, OSDMap characters)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            RegionObjects = regionObjects ?? throw new ArgumentNullException(nameof(regionObjects));
            TerrainProperties = terrainProperties ?? throw new ArgumentNullException(nameof(terrainProperties));
            Characters = characters ?? throw new ArgumentNullException(nameof(characters));
            if (regionObjects.Count > 20000 || characters.Count > 4096 ||
                terrainProperties.Count > 64)
                throw new ArgumentException("NavMesh metadata exceeded safe region limits.");
        }
    }

    /// <summary>
    /// Firestorm pathfinding CAPS bridge. Never advertise RetrieveNavMeshSrc
    /// until a separate trusted publisher has delivered real native viewer
    /// bytes AND the region navigation graph is current. No fake CAPS, no
    /// destructive Rebake PUT and no unsupported object property writes.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglFirestormNavMeshCapsModule")]
    public sealed class OglFirestormNavMeshCapsModule : ISharedRegionModule
    {
        private sealed class RegionState
        {
            public readonly Scene Scene;
            public readonly EventManager.RegisterCapsEvent CapsListener;
            public RegionState(Scene scene, OglFirestormNavMeshCapsModule module)
            {
                Scene = scene;
                CapsListener = (avatar, caps) => module.RegisterCaps(this, avatar, caps);
            }
        }

        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(OglFirestormNavMeshCapsModule));
        private readonly ConcurrentDictionary<UUID, RegionState> m_Regions = new();
        private bool m_Enabled;

        public string Name => "OGL Firestorm NavMesh CAPS (verified source only)";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig section = config?.Configs["OGLPathfinding"];
            m_Enabled = section?.GetBoolean("Enabled", false) == true &&
                section.GetBoolean("FirestormNavMeshCaps", false);
        }
        public void PostInitialise() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null) return;
            RegionState state = new(scene, this);
            if (m_Regions.TryAdd(scene.RegionInfo.RegionID, state))
            {
                scene.EventManager.OnRegisterCaps += state.CapsListener;
                m_Log.InfoFormat(
                    "[OGL-NAVMESH]: Region {0} registered verified-source CAPS bridge. " +
                    "Viewer CAPS remain unadvertised until native NavMesh data is available.",
                    scene.Name);
            }
        }
        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (scene != null && m_Regions.TryRemove(
                scene.RegionInfo.RegionID, out RegionState state))
                scene.EventManager.OnRegisterCaps -= state.CapsListener;
        }

        public void Close()
        {
            foreach (RegionState state in m_Regions.Values)
                state.Scene.EventManager.OnRegisterCaps -= state.CapsListener;
            m_Regions.Clear();
        }

        private static bool TryCapture(RegionState state,
            out OglFirestormNavMeshSnapshot snapshot)
        {
            snapshot = null;
            // Require native navigator freshness; publication is invalidated
            // immediately when terrain or scene objects change.
            IOglTerrainNavigationRegion navigation =
                state.Scene.RequestModuleInterface<IOglTerrainNavigationRegion>();
            if (navigation == null || !navigation.IsNavigationReady ||
                navigation.IsNavigationDirty)
                return false;

            IOglFirestormNavMeshSource source =
                state.Scene.RequestModuleInterface<IOglFirestormNavMeshSource>();
            if (source == null || !source.TryCaptureFirestormSnapshot(
                state.Scene, out snapshot) || snapshot == null ||
                snapshot.Transport == null)
            {
                snapshot = null;
                return false;
            }
            // Check the navigation epoch again after the source completed
            // its possibly expensive capture.
            if (!navigation.IsNavigationReady || navigation.IsNavigationDirty)
            {
                snapshot = null;
                return false;
            }
            return true;
        }

        private void RegisterCaps(RegionState state, UUID avatar, Caps caps)
        {
            if (!m_Enabled || caps == null ||
                !m_Regions.ContainsKey(state.Scene.RegionInfo.RegionID))
                return;
            try
            {
                // No source, no viewer capabilities: an empty NavMesh CAP
                // would activate menus that cannot actually function.
                if (!TryCapture(state, out _)) return;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[OGL-NAVMESH]: Native data unavailable at CAPS registration: {0}",
                    e.Message);
                return;
            }
            RegisterReadOnlyCap("RetrieveNavMeshSrc", state, avatar, caps);
            RegisterReadOnlyCap("NavMeshGenerationStatus", state, avatar, caps);
            RegisterReadOnlyCap("AgentState", state, avatar, caps);
            RegisterReadOnlyCap("RegionObjects", state, avatar, caps);
            RegisterReadOnlyCap("TerrainNavMeshProperties", state, avatar, caps);
            RegisterReadOnlyCap("CharacterProperties", state, avatar, caps);

            m_Log.InfoFormat(
                "[OGL-NAVMESH]: Verified Firestorm read-only CAPS available " +
                "for agent {0} in {1}. Rebuild and edit remain disabled.",
                avatar, state.Scene.Name);
        }

        private void RegisterReadOnlyCap(string name, RegionState state,
            UUID avatar, Caps caps)
        {
            caps.RegisterSimpleHandler(name, new SimpleStreamHandler(
                "/" + UUID.Random(),
                (request, response) => Handle(state, avatar, name, request, response)));
        }

        private static void WriteLlsd(IOSHttpResponse response, OSD payload)
        {
            string llsd = OSDParser.SerializeLLSDXmlString(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(llsd);
            if (bytes.Length > 16 * 1024 * 1024)
            {
                response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return;
            }
            response.RawBuffer = bytes;
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void Handle(RegionState state, UUID avatar, string name,
            IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/llsd+xml";
            response.AddHeader("Cache-Control", "no-store");
            // RetrieveNavMeshSrc is POST with a zero-content LLSD body.
            bool isNavmesh = name == "RetrieveNavMeshSrc";
            if (!string.Equals(request.HttpMethod, isNavmesh ? "POST" : "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (!m_Regions.TryGetValue(state.Scene.RegionInfo.RegionID,
                    out RegionState liveState) ||
                !ReferenceEquals(state, liveState) ||
                !state.Scene.TryGetScenePresence(avatar, out ScenePresence agent) ||
                agent == null || agent.IsDeleted || agent.IsNPC ||
                agent.IsChildAgent || agent.ControllingClient == null)
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }
            try
            {
                if (!TryCapture(state, out OglFirestormNavMeshSnapshot snapshot))
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    return;
                }
                OSDMap data;
                switch (name)
                {
                    case "NavMeshGenerationStatus":
                        data = new OSDMap
                        {
                            ["region_id"] = OSD.FromUUID(state.Scene.RegionInfo.RegionID),
                            ["version"] = OSD.FromInteger(snapshot.Transport.Version),
                            ["status"] = OSD.FromString("complete")
                        };
                        break;
                    case "RetrieveNavMeshSrc":
                        data = new OSDMap
                        {
                            ["navmesh_version"] = OSD.FromInteger(snapshot.Transport.Version),
                            ["navmesh_data"] = OSD.FromBinary(
                                snapshot.Transport.CopyCompressedBytes())
                        };
                        break;
                    case "AgentState":
                        // Never claim that the viewer can rebake a NavMesh:
                        // no safe generation/write endpoint exists yet.
                        data = new OSDMap
                        {
                            ["can_modify_navmesh"] = OSD.FromBoolean(false)
                        };
                        break;
                    case "RegionObjects":
                        data = snapshot.RegionObjects;
                        break;
                    case "TerrainNavMeshProperties":
                        data = snapshot.TerrainProperties;
                        break;
                    case "CharacterProperties":
                        data = snapshot.Characters;
                        break;
                    default:
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                }
                WriteLlsd(response, data);
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[OGL-NAVMESH]: Firestorm {0} read failed closed for region {1}: {2}",
                    name, state.Scene.Name, e.Message);
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            }
        }
    }
}
