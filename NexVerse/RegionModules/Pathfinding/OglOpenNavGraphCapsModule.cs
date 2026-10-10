// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
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
    /// Read-only, session-scoped OpenGenesisLINK NAVGRAPH/1 capability.
    /// Firestorm cannot decode this custom format without a viewer fork:
    /// NEVER advertise this data under RetrieveNavMeshSrc.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglOpenNavGraphCapsModule")]
    public sealed class OglOpenNavGraphCapsModule : ISharedRegionModule
    {
        private sealed class State
        {
            public readonly Scene Scene;
            public readonly EventManager.RegisterCapsEvent Listener;
            public State(Scene scene, OglOpenNavGraphCapsModule owner)
            {
                Scene = scene;
                Listener = (agent, caps) => owner.Register(this, agent, caps);
            }
        }

        private sealed class Session
        {
            public readonly UUID Avatar;
            private readonly OglNavGraphRateGate m_ReadGate = new();
            public Session(UUID avatar) { Avatar = avatar; }
            public bool TryAcquire() =>
                m_ReadGate.TryAcquire(Environment.TickCount64, 3000);
        }

        private static readonly ILog Log =
            LogManager.GetLogger(typeof(OglOpenNavGraphCapsModule));
        private readonly ConcurrentDictionary<UUID, State> m_Regions = new();
        private bool m_Enabled;

        public string Name => "OGL Native NavGraph/1 CAPS (viewer fork only)";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig section = config?.Configs["OGLPathfinding"];
            m_Enabled = section?.GetBoolean("Enabled", false) == true &&
                section.GetBoolean("UseVerifiedLayeredSurfaces", false) &&
                section.GetBoolean("OpenNavGraphCaps", false);
        }

        public void PostInitialise() { }
        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null) return;
            State state = new(scene, this);
            if (m_Regions.TryAdd(scene.RegionInfo.RegionID, state))
                scene.EventManager.OnRegisterCaps += state.Listener;
        }
        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (scene != null && m_Regions.TryRemove(
                scene.RegionInfo.RegionID, out State state))
                state.Scene.EventManager.OnRegisterCaps -= state.Listener;
        }
        public void Close()
        {
            foreach (State state in m_Regions.Values)
                state.Scene.EventManager.OnRegisterCaps -= state.Listener;
            m_Regions.Clear();
        }

        private void Register(State state, UUID avatar, Caps caps)
        {
            if (!m_Enabled || caps == null ||
                !m_Regions.TryGetValue(state.Scene.RegionInfo.RegionID,
                    out State live) || !ReferenceEquals(state, live))
                return;
            // Register even when a bake is pending; return HTTP 503 in that
            // case. The viewer must not permanently lose the cap on login.
            Session session = new(avatar);
            caps.RegisterSimpleHandler("OGLRetrieveNavGraph", new SimpleStreamHandler(
                "/" + UUID.Random(),
                (request, response) => ReadGraph(state, session, request, response)));
        }

        private void ReadGraph(State state, Session session,
            IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/llsd+xml";
            response.AddHeader("Cache-Control", "no-store");

            if (!string.Equals(request.HttpMethod, "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (!m_Regions.TryGetValue(state.Scene.RegionInfo.RegionID,
                    out State live) || !ReferenceEquals(state, live) ||
                !state.Scene.TryGetScenePresence(session.Avatar,
                    out ScenePresence presence) || presence == null ||
                presence.IsDeleted || presence.IsNPC || presence.IsChildAgent ||
                presence.ControllingClient == null)
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }
            if (!session.TryAcquire())
            {
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                return;
            }

            IOglCertifiedNavGraphRegion provider =
                state.Scene.RequestModuleInterface<IOglCertifiedNavGraphRegion>();
            if (provider == null || !provider.TryCaptureCertifiedGraph(
                    out OglLayeredNavGraph graph, out int revision))
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }
            try
            {
                byte[] payload = OglOpenNavGraphCodec.Encode(graph, revision);
                if (!provider.TryCaptureCertifiedGraph(
                        out OglLayeredNavGraph current, out int currentRevision) ||
                    !ReferenceEquals(current, graph) || revision != currentRevision)
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    return;
                }
                OSDMap data = new()
                {
                    ["format"] = OSD.FromString("OGLNAVGRAPH/1"),
                    ["revision"] = OSD.FromInteger(revision),
                    ["region_id"] = OSD.FromUUID(state.Scene.RegionInfo.RegionID),
                    ["data"] = OSD.FromBinary(payload)
                };
                string xml = OSDParser.SerializeLLSDXmlString(data);
                byte[] bytes = Encoding.UTF8.GetBytes(xml);
                if (bytes.Length > 18 * 1024 * 1024)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                    return;
                }
                response.RawBuffer = bytes;
                response.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception error)
            {
                Log.WarnFormat(
                    "[OGL-NAVGRAPH]: Transport rejected region {0}: {1}",
                    state.Scene.Name, error.Message);
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            }
        }
    }
}
