// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using log4net;
using Mono.Addins;
using NexVerse.Core.Pathfinding;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// Opt-in NPC waypoint follower using the EXISTING OpenSimulator NPC
    /// motor/physics. No LSL parity claim or unsafe direct position changes.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglNpcPathFollowerModule")]
    public sealed class OglNpcPathFollowerModule : ISharedRegionModule
    {
        private sealed class Job
        {
            public readonly UUID Caller;
            public readonly OglNpcWaypointCursor Cursor;
            public Job(UUID caller, OglNpcWaypointCursor cursor)
            {
                Caller = caller;
                Cursor = cursor;
            }
        }

        private sealed class RegionRoutes : IOglNpcRouteService
        {
            public readonly Scene Scene;
            private readonly object m_Sync = new();
            private readonly Dictionary<UUID, Job> m_Jobs = new();
            private readonly int m_MaxActive;
            private readonly int m_MaxWaypoints;
            private readonly TimeSpan m_WaypointTimeout;
            public bool Disposed;

            public RegionRoutes(Scene scene, int maxActive, int maxWaypoints,
                TimeSpan waypointTimeout)
            {
                Scene = scene;
                m_MaxActive = maxActive;
                m_MaxWaypoints = maxWaypoints;
                m_WaypointTimeout = waypointTimeout;
            }

            public int ActiveRouteCount
            {
                get { lock (m_Sync) return m_Jobs.Count; }
            }

            public bool TryNavigate(UUID npcId, UUID callerId, Vector3 destination,
                out string reason)
            {
                reason = "navigation_unavailable";
                if (Disposed || npcId.IsZero() || callerId.IsZero() || !float.IsFinite(destination.X) ||
                    !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z))
                    return false;

                INPCModule npc = Scene.RequestModuleInterface<INPCModule>();
                IOglTerrainNavigationRegion nav =
                    Scene.RequestModuleInterface<IOglTerrainNavigationRegion>();
                if (npc == null || nav?.IsNavigationReady != true ||
                    !npc.IsNPC(npcId, Scene) ||
                    !npc.CheckPermissions(npcId, callerId) ||
                    !Scene.TryGetScenePresence(npcId, out ScenePresence sp) ||
                    sp == null || sp.IsDeleted || !sp.IsNPC)
                {
                    reason = "npc_permission_or_navigation_missing";
                    return false;
                }

                if (!nav.TryFindNpcPath(sp.AbsolutePosition.X,
                    sp.AbsolutePosition.Y, sp.AbsolutePosition.Z,
                    destination.X, destination.Y, destination.Z,
                    out IReadOnlyList<OglNavigationPoint> points))
                {
                    reason = "route_not_found";
                    return false;
                }

                OglNpcWaypointCursor cursor;
                try
                {
                    cursor = new OglNpcWaypointCursor(points,
                        DateTimeOffset.UtcNow, m_MaxWaypoints);
                }
                catch (ArgumentException)
                {
                    reason = "route_too_long_or_invalid";
                    return false;
                }

                lock (m_Sync)
                {
                    if (Disposed || !nav.IsNavigationReady)
                    {
                        reason = "navigation_invalidated";
                        return false;
                    }
                    if (!m_Jobs.ContainsKey(npcId) && m_Jobs.Count >= m_MaxActive)
                    {
                        reason = "npc_route_quota";
                        return false;
                    }

                    cursor.UpdatePosition3D(sp.AbsolutePosition.X,
                        sp.AbsolutePosition.Y, sp.AbsolutePosition.Z,
                        2.0f, DateTimeOffset.UtcNow);
                    if (!cursor.Completed)
                    {
                        OglNavigationPoint p = cursor.Current;
                        if (!npc.MoveToTarget(npcId, Scene,
                            new Vector3(p.X, p.Y, p.Z + 1.0f),
                            true, true, false))
                        {
                            reason = "npc_move_rejected";
                            return false;
                        }
                        m_Jobs[npcId] = new Job(callerId, cursor);
                    }
                    else
                    {
                        npc.StopMoveToTarget(npcId, Scene);
                        m_Jobs.Remove(npcId);
                    }
                }

                reason = "accepted";
                return true;
            }

            public bool Stop(UUID npcId, UUID callerId)
            {
                INPCModule npc = Scene.RequestModuleInterface<INPCModule>();
                if (callerId.IsZero() || npc == null || !npc.CheckPermissions(npcId, callerId))
                    return false;
                lock (m_Sync)
                {
                    if (!m_Jobs.Remove(npcId))
                        return false;
                    npc.StopMoveToTarget(npcId, Scene);
                    return true;
                }
            }

            public void Tick()
            {
                lock (m_Sync)
                {
                    if (Disposed || m_Jobs.Count == 0)
                        return;

                    INPCModule npc = Scene.RequestModuleInterface<INPCModule>();
                    IOglTerrainNavigationRegion nav =
                        Scene.RequestModuleInterface<IOglTerrainNavigationRegion>();
                    List<UUID> removed = new();
                    foreach (KeyValuePair<UUID, Job> pair in m_Jobs)
                    {
                        UUID id = pair.Key;
                        Job job = pair.Value;
                        if (npc == null || !npc.IsNPC(id, Scene) ||
                            !npc.CheckPermissions(id, job.Caller) ||
                            !Scene.TryGetScenePresence(id, out ScenePresence sp) ||
                            sp == null || sp.IsDeleted ||
                            nav?.IsNavigationReady != true ||
                            job.Cursor.Stalled(DateTimeOffset.UtcNow, m_WaypointTimeout))
                        {
                            if (npc != null && npc.IsNPC(id, Scene))
                                npc.StopMoveToTarget(id, Scene);
                            removed.Add(id);
                            continue;
                        }

                        if (!job.Cursor.UpdatePosition3D(sp.AbsolutePosition.X,
                            sp.AbsolutePosition.Y, sp.AbsolutePosition.Z,
                            2.0f, DateTimeOffset.UtcNow))
                            continue;

                        if (job.Cursor.Completed)
                        {
                            npc.StopMoveToTarget(id, Scene);
                            removed.Add(id);
                        }
                        else
                        {
                            OglNavigationPoint next = job.Cursor.Current;
                            if (!npc.MoveToTarget(id, Scene,
                                new Vector3(next.X, next.Y, next.Z + 1.0f),
                                true, true, false))
                            {
                                npc.StopMoveToTarget(id, Scene);
                                removed.Add(id);
                            }
                        }
                    }

                    foreach (UUID id in removed)
                        m_Jobs.Remove(id);
                }
            }

            public void Dispose()
            {
                lock (m_Sync)
                {
                    Disposed = true;
                    INPCModule npc = Scene.RequestModuleInterface<INPCModule>();
                    foreach (UUID id in m_Jobs.Keys)
                        if (npc != null && npc.IsNPC(id, Scene))
                            npc.StopMoveToTarget(id, Scene);
                    m_Jobs.Clear();
                }
            }
        }

        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(OglNpcPathFollowerModule));
        private readonly ConcurrentDictionary<UUID, RegionRoutes> m_Regions = new();
        private bool m_Enabled;
        private int m_MaxActive = 32;
        private int m_MaxWaypoints = 256;
        private int m_TickMs = 1000;
        private int m_WaypointWaitSeconds = 45;
        private int m_Ticking;
        private int m_Closed;
        private Timer m_Timer;

        public string Name => "OGL NPC Path Follower (experimental)";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig settings = config.Configs["OGLNpcNavigation"];
            m_Enabled = settings?.GetBoolean("Enabled", false) ?? false;
            if (!m_Enabled) return;
            m_MaxActive = Math.Clamp(settings.GetInt("MaxActivePerRegion", 32), 1, 64);
            m_MaxWaypoints = Math.Clamp(settings.GetInt("MaxWaypoints", 256), 8, 256);
            m_TickMs = Math.Clamp(settings.GetInt("TickMilliseconds", 1000), 250, 5000);
            m_WaypointWaitSeconds = Math.Clamp(settings.GetInt("WaypointTimeoutSeconds", 45),
                10, 120);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null) return;
            RegionRoutes routes = new(scene, m_MaxActive, m_MaxWaypoints,
                TimeSpan.FromSeconds(m_WaypointWaitSeconds));
            if (m_Regions.TryAdd(scene.RegionInfo.RegionID, routes))
                scene.RegisterModuleInterface<IOglNpcRouteService>(routes);
        }

        public void RegionLoaded(Scene scene) { }

        public void PostInitialise()
        {
            if (!m_Enabled) return;
            m_Timer = new Timer(_ => TickAll(), null,
                TimeSpan.FromMilliseconds(m_TickMs),
                TimeSpan.FromMilliseconds(m_TickMs));
        }

        private void TickAll()
        {
            if (Volatile.Read(ref m_Closed) != 0 ||
                Interlocked.Exchange(ref m_Ticking, 1) != 0)
                return;
            try
            {
                foreach (RegionRoutes route in m_Regions.Values)
                {
                    try { route.Tick(); }
                    catch (Exception e)
                    {
                        m_Log.Error("[OGL-PATH]: NPC route scheduler failure.", e);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref m_Ticking, 0);
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene != null &&
                m_Regions.TryRemove(scene.RegionInfo.RegionID, out RegionRoutes routes))
            {
                scene.UnregisterModuleInterface<IOglNpcRouteService>(routes);
                routes.Dispose();
            }
        }

        public void Close()
        {
            Interlocked.Exchange(ref m_Closed, 1);
            m_Timer?.Dispose();
            foreach (RegionRoutes routes in m_Regions.Values)
            {
                routes.Scene.UnregisterModuleInterface<IOglNpcRouteService>(routes);
                routes.Dispose();
            }
            m_Regions.Clear();
        }
    }
}
