// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// Experimental, opt-in terrain-only navigation. Never moves an avatar,
    /// never overrides physics, and does not claim LSL pathfinding parity.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglTerrainNavigationModule")]
    public sealed class OglTerrainNavigationModule : ISharedRegionModule
    {
        private sealed class RegionNavigation : IOglTerrainNavigationRegion
        {
            public readonly Scene Scene;
            public readonly EventManager.OnTerrainTaintedDelegate Listener;
            private readonly int m_MaxExpanded;
            internal OglTerrainNavigationSnapshot Snapshot;
            internal int Dirty = 1;
            internal int Building;

            public RegionNavigation(Scene scene, int maxExpanded)
            {
                Scene = scene;
                m_MaxExpanded = maxExpanded;
                Listener = () => Interlocked.Exchange(ref Dirty, 1);
            }

            public bool IsNavigationReady => Volatile.Read(ref Snapshot) != null &&
                Volatile.Read(ref Dirty) == 0;
            public bool IsNavigationDirty => Volatile.Read(ref Dirty) != 0;

            public bool TryFindTerrainPath(float startX, float startY, float targetX, float targetY,
                out IReadOnlyList<OglNavigationPoint> path)
            {
                path = Array.Empty<OglNavigationPoint>();
                if (IsNavigationDirty)
                    return false;
                OglTerrainNavigationSnapshot snapshot = Volatile.Read(ref Snapshot);
                return snapshot != null && snapshot.TryFindWorldPath(startX, startY,
                    targetX, targetY, out path, m_MaxExpanded);
            }
        }

        private static readonly ILog m_Log = LogManager.GetLogger(typeof(OglTerrainNavigationModule));
        private readonly ConcurrentDictionary<UUID, RegionNavigation> m_Regions = new();
        private bool m_Enabled;
        private int m_CellMeters = 4;
        private float m_MaxSlope = 0.65f;
        private int m_MaxExpanded = 20000;
        private int m_RebuildSeconds = 30;
        private Timer m_Timer;
        private int m_Closed;

        public string Name => "OGL Terrain Navigation (experimental)";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["OGLPathfinding"];
            m_Enabled = config?.GetBoolean("Enabled", false) ?? false;
            if (!m_Enabled)
            {
                m_Log.Info("[OGL-PATH]: Terrain A* disabled by [OGLPathfinding] (Enabled=false). Firestorm RetrieveNavMeshSrc requires a separate Second Life-compatible NavMesh implementation.");
                return;
            }
            m_CellMeters = Math.Clamp(config.GetInt("CellMeters", 4), 1, 32);
            m_MaxSlope = Math.Clamp(config.GetFloat("MaxSlopePerMeter", 0.65f), 0.05f, 2.0f);
            m_MaxExpanded = Math.Clamp(config.GetInt("MaxExpandedNodes", 20000), 128, 100000);
            m_RebuildSeconds = Math.Clamp(config.GetInt("RebuildSeconds", 30), 15, 3600);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;
            RegionNavigation state = new(scene, m_MaxExpanded);
            if (!m_Regions.TryAdd(scene.RegionInfo.RegionID, state))
                return;
            scene.EventManager.OnTerrainTainted += state.Listener;
            scene.RegisterModuleInterface<IOglTerrainNavigationRegion>(state);
            m_Log.InfoFormat(
                "[OGL-PATH]: Region {0} native terrain A* configured (cell={1}m, maxExpanded={2}). Firestorm RetrieveNavMeshSrc intentionally not advertised: Havok-compatible mesh payload/status not implemented.",
                scene.Name, m_CellMeters, m_MaxExpanded);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled || scene == null ||
                !m_Regions.TryGetValue(scene.RegionInfo.RegionID, out RegionNavigation state))
                return;
            // Controlled initial snapshot. Every subsequent terrain edit causes
            // a dirty flag, with asynchronous refresh on a bounded timer.
            Rebuild(state);
        }

        public void PostInitialise()
        {
            if (!m_Enabled) return;
            m_Timer = new Timer(_ => RefreshDirty(), null,
                TimeSpan.FromSeconds(m_RebuildSeconds),
                TimeSpan.FromSeconds(m_RebuildSeconds));
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null || !m_Regions.TryRemove(scene.RegionInfo.RegionID,
                out RegionNavigation state))
                return;
            Interlocked.Exchange(ref state.Dirty, 1);
            scene.EventManager.OnTerrainTainted -= state.Listener;
            scene.UnregisterModuleInterface<IOglTerrainNavigationRegion>(state);
        }

        private void RefreshDirty()
        {
            if (Volatile.Read(ref m_Closed) != 0)
                return;
            foreach (RegionNavigation state in m_Regions.Values)
            {
                if (Volatile.Read(ref state.Dirty) == 0)
                    continue;
                // Rebuild one region asynchronously, never overlapping its
                // previous build. Avoid stalls in the simulator scene tick.
                _ = Task.Run(() => Rebuild(state));
            }
        }

        private void Rebuild(RegionNavigation state)
        {
            if (Volatile.Read(ref m_Closed) != 0 ||
                Interlocked.Exchange(ref state.Building, 1) != 0)
                return;
            try
            {
                if (state.Scene.Heightmap == null)
                    return;

                Interlocked.Exchange(ref state.Dirty, 0);
                ITerrainChannel copy = state.Scene.Heightmap.MakeCopy();
                int width = checked((int)state.Scene.RegionInfo.RegionSizeX);
                int height = checked((int)state.Scene.RegionInfo.RegionSizeY);
                if (copy == null || copy.Width != width || copy.Height != height)
                    throw new InvalidOperationException("Terrain channel dimensions differ from region dimensions");

                float water = (float)state.Scene.RegionInfo.RegionSettings.WaterHeight;
                OglTerrainNavigationSnapshot snapshot = OglTerrainNavigationSnapshot.Build(
                    width, height, m_CellMeters, water, m_MaxSlope, (x, y) => copy[x, y]);
                // Invalidation can arrive while building. In that case the
                // freshly built candidate remains unavailable until next refresh.
                Volatile.Write(ref state.Snapshot, snapshot);
                if (Volatile.Read(ref state.Dirty) == 0)
                    m_Log.InfoFormat("[OGL-PATH]: Terrain navigation ready for region {0} ({1}x{2}, cell {3}m). This is NOT a Firestorm NavMesh.",
                        state.Scene.Name, width, height, m_CellMeters);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref state.Dirty, 1);
                m_Log.WarnFormat("[OGL-PATH]: Terrain-Navigation konnte nicht aktualisiert werden: {0}", e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref state.Building, 0);
            }
        }

        public void Close()
        {
            Interlocked.Exchange(ref m_Closed, 1);
            m_Timer?.Dispose();
            foreach (RegionNavigation state in m_Regions.Values)
            {
                Interlocked.Exchange(ref state.Dirty, 1);
                state.Scene.EventManager.OnTerrainTainted -= state.Listener;
                state.Scene.UnregisterModuleInterface<IOglTerrainNavigationRegion>(state);
            }
            m_Regions.Clear();
        }
    }
}
