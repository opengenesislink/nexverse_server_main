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
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// Contract implemented by a trusted Scene/Physics adapter. The adapter
    /// MUST sample actual collision-backed walkable surfaces, validate every
    /// explicit off-mesh portal and return a fresh immutable graph. AABB
    /// extents alone do not establish floors, stairs or walkable mesh tops.
    /// The terrain navigator does not fabricate missing geometry.
    /// </summary>
    public interface IOglVerifiedLayeredSurfaceSource
    {
        OglLayeredNavGraph CaptureVerifiedGraph(
            Scene scene, int cellMeters, float maxStepMeters);
    }

    /// <summary>
    /// Experimental opt-in terrain navigation, optionally backed by verified
    /// multi-layer Scene surfaces. Never overrides avatar or NPC physics.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglTerrainNavigationModule")]
    public sealed class OglTerrainNavigationModule : ISharedRegionModule
    {
        private sealed class RegionNavigation : IOglTerrainNavigationRegion,
            IOglNativeTerrainQuery, IOglCertifiedNavGraphRegion
        {
            public readonly Scene Scene;
            public readonly EventManager.OnTerrainTaintedDelegate Listener;
            public readonly Action<SceneObjectGroup> StaticAdded;
            public readonly EventManager.ObjectBeingRemovedFromScene StaticRemoved;
            public readonly EventManager.SceneObjectPartUpdated StaticUpdated;
            private readonly int m_MaxExpanded;
            private readonly bool m_RequireLayered;
            internal OglTerrainNavigationSnapshot Snapshot;
            internal OglLayeredNavGraph LayeredGraph;
            // Every terrain mutation advances the epoch, including during a
            // build. A stale computed snapshot can NEVER become ready again.
            internal int Epoch = 1;
            internal int SnapshotEpoch;
            internal int Dirty = 1;
            internal int Building;

            public RegionNavigation(Scene scene, int maxExpanded, bool requireLayered)
            {
                Scene = scene;
                m_MaxExpanded = maxExpanded;
                m_RequireLayered = requireLayered;
                Listener = Invalidate;
                StaticAdded = _ => Invalidate();
                StaticRemoved = _ => Invalidate();
                StaticUpdated = (part, full) =>
                {
                    if (part?.ParentGroup != null)
                        Invalidate();
                };
            }

            public void Invalidate()
            {
                Interlocked.Increment(ref Epoch);
                Volatile.Write(ref Dirty, 1);
            }

            public bool TryCaptureCertifiedGraph(
                out OglLayeredNavGraph graph, out int revision)
            {
                graph = null;
                revision = 0;
                if (!m_RequireLayered) return false;
                int epoch = Volatile.Read(ref Epoch);
                OglLayeredNavGraph candidate = Volatile.Read(ref LayeredGraph);
                if (candidate == null || IsNavigationDirty ||
                    Volatile.Read(ref SnapshotEpoch) != epoch ||
                    !candidate.TryCaptureCertifiedArcs(out _) ||
                    IsNavigationDirty || Volatile.Read(ref Epoch) != epoch)
                    return false;
                graph = candidate;
                revision = epoch;
                return true;
            }

            public bool IsNavigationReady =>
                Volatile.Read(ref Snapshot) != null &&
                (!m_RequireLayered || Volatile.Read(ref LayeredGraph) != null) &&
                !IsNavigationDirty;
            public bool IsNavigationDirty =>
                Volatile.Read(ref Dirty) != 0 ||
                Volatile.Read(ref Building) != 0 ||
                Volatile.Read(ref SnapshotEpoch) != Volatile.Read(ref Epoch);

            public bool TryGetClosestNavPoint(float x, float y, float z,
                float radius, out Vector3 nearest)
            {
                nearest = Vector3.Zero;
                if (m_RequireLayered)
                {
                    int epoch = Volatile.Read(ref Epoch);
                    OglLayeredNavGraph graph = Volatile.Read(ref LayeredGraph);
                    if (graph == null || IsNavigationDirty ||
                        epoch != Volatile.Read(ref SnapshotEpoch) ||
                        !graph.TryFindClosestSurface(x, y, z, radius,
                            0.5f, out int index) ||
                        IsNavigationDirty || epoch != Volatile.Read(ref Epoch))
                        return false;
                    OglLayerNavNode node = graph.GetNode(index);
                    nearest = new Vector3(
                        (node.X + 0.5f) * graph.CellMeters,
                        (node.Y + 0.5f) * graph.CellMeters, node.Z);
                    return true;
                }
                if (!TryFindNearestTerrainPoint(x, y, z, radius,
                    out OglNavigationPoint point))
                    return false;
                nearest = new Vector3(point.X, point.Y, point.Z);
                return true;
            }

            public bool TryFindNearestTerrainPoint(float x, float y, float z,
                float radius, out OglNavigationPoint point)
            {
                point = default;
                int epoch = Volatile.Read(ref Epoch);
                OglTerrainNavigationSnapshot snapshot = Volatile.Read(ref Snapshot);
                if (snapshot == null || IsNavigationDirty ||
                    epoch != Volatile.Read(ref SnapshotEpoch))
                    return false;
                if (!snapshot.TryFindNearestTerrainPoint(x, y, z, radius, out point))
                    return false;
                if (IsNavigationDirty || epoch != Volatile.Read(ref Epoch))
                {
                    point = default;
                    return false;
                }
                return true;
            }

            public bool TryGetStaticTerrainPath(Vector3 start, Vector3 end,
                float radius, out Vector3[] waypoints, out int status)
            {
                waypoints = Array.Empty<Vector3>();
                status = 9; // PU_FAILURE_NO_NAVMESH for missing/dirty snapshot.
                int epoch = Volatile.Read(ref Epoch);
                OglTerrainNavigationSnapshot snapshot = Volatile.Read(ref Snapshot);
                if (snapshot == null || IsNavigationDirty ||
                    epoch != Volatile.Read(ref SnapshotEpoch))
                    return false;
                IReadOnlyList<OglNavigationPoint> route;
                if (m_RequireLayered)
                {
                    OglLayeredNavGraph graph = Volatile.Read(ref LayeredGraph);
                    if (graph == null ||
                        !graph.TryFindWorldPath(
                            start.X, start.Y, start.Z, end.X, end.Y, end.Z,
                            radius, out route, out status, m_MaxExpanded))
                        return false;
                }
                else if (!snapshot.TryFindStaticTerrainRoute(
                    start.X, start.Y, start.Z,
                    end.X, end.Y, end.Z, radius,
                    out route, out status, m_MaxExpanded))
                    return false;

                Vector3[] result = new Vector3[route.Count];
                for (int i = 0; i < result.Length; ++i)
                    result[i] = new Vector3(route[i].X, route[i].Y, route[i].Z);
                if (IsNavigationDirty || epoch != Volatile.Read(ref Epoch))
                {
                    status = 9;
                    return false;
                }
                waypoints = result;
                status = 0;
                return true;
            }

            public bool TryFindTerrainPath(float startX, float startY, float targetX, float targetY,
                out IReadOnlyList<OglNavigationPoint> path)
            {
                path = Array.Empty<OglNavigationPoint>();
                int requestedEpoch = Volatile.Read(ref Epoch);
                OglTerrainNavigationSnapshot snapshot = Volatile.Read(ref Snapshot);
                if (snapshot == null || IsNavigationDirty ||
                    requestedEpoch != Volatile.Read(ref SnapshotEpoch))
                    return false;
                if (!snapshot.TryFindWorldPath(startX, startY,
                    targetX, targetY, out path, m_MaxExpanded))
                    return false;
                // Terraforming may invalidate the route DURING the bounded A*
                // search. Never return a path from that stale snapshot.
                if (IsNavigationDirty || requestedEpoch != Volatile.Read(ref Epoch))
                {
                    path = Array.Empty<OglNavigationPoint>();
                    return false;
                }
                return true;
            }
        }

        private static readonly ILog m_Log = LogManager.GetLogger(typeof(OglTerrainNavigationModule));
        private readonly ConcurrentDictionary<UUID, RegionNavigation> m_Regions = new();
        private bool m_Enabled;
        private int m_CellMeters = 4;
        private float m_MaxSlope = 0.65f;
        private int m_MaxExpanded = 20000;
        private int m_RebuildSeconds = 30;
        private bool m_TrackStaticColliders;
        private bool m_UseLayeredSurfaces;
        private float m_LayeredMaxStep = 0.6f;
        private float m_StaticAgentHeight = 1.8f;
        private int m_MaxStaticPrims = 20000;
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
            m_TrackStaticColliders = config.GetBoolean("TrackStaticColliders", false);
            m_UseLayeredSurfaces = config.GetBoolean("UseVerifiedLayeredSurfaces", false);
            m_LayeredMaxStep = Math.Clamp(
                config.GetFloat("LayeredMaxStepMeters", 0.6f), 0.1f, 3f);
            m_StaticAgentHeight = Math.Clamp(config.GetFloat("StaticAgentHeight", 1.8f), 0.5f, 5f);
            m_MaxStaticPrims = Math.Clamp(config.GetInt("MaxStaticPrims", 20000), 1, 50000);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;
            RegionNavigation state = new(scene, m_MaxExpanded, m_UseLayeredSurfaces);
            if (!m_Regions.TryAdd(scene.RegionInfo.RegionID, state))
                return;
            scene.EventManager.OnTerrainTainted += state.Listener;
            if (m_TrackStaticColliders || m_UseLayeredSurfaces)
            {
                scene.EventManager.OnObjectAddedToScene += state.StaticAdded;
                scene.EventManager.OnObjectBeingRemovedFromScene += state.StaticRemoved;
                scene.EventManager.OnSceneObjectPartUpdated += state.StaticUpdated;
            }
            scene.RegisterModuleInterface<IOglTerrainNavigationRegion>(state);
            scene.RegisterModuleInterface<IOglNativeTerrainQuery>(state);
            if (m_UseLayeredSurfaces)
                scene.RegisterModuleInterface<IOglCertifiedNavGraphRegion>(state);
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
            Interlocked.Increment(ref state.Epoch);
            Volatile.Write(ref state.Dirty, 1);
            scene.EventManager.OnTerrainTainted -= state.Listener;
            if (m_TrackStaticColliders || m_UseLayeredSurfaces)
            {
                scene.EventManager.OnObjectAddedToScene -= state.StaticAdded;
                scene.EventManager.OnObjectBeingRemovedFromScene -= state.StaticRemoved;
                scene.EventManager.OnSceneObjectPartUpdated -= state.StaticUpdated;
            }
            scene.UnregisterModuleInterface<IOglTerrainNavigationRegion>(state);
            scene.UnregisterModuleInterface<IOglNativeTerrainQuery>(state);
            if (m_UseLayeredSurfaces)
                scene.UnregisterModuleInterface<IOglCertifiedNavGraphRegion>(state);
        }

        private static IReadOnlyList<OglStaticCollisionAabb> CaptureStaticColliders(
            Scene scene, int maxPrims)
        {
            List<OglStaticCollisionAabb> bounds = new();
            foreach (SceneObjectGroup group in scene.GetSceneObjectGroups())
            {
                if (group == null || group.IsDeleted || group.IsAttachmentCheckFull() ||
                    group.IsPhantom || group.IsVolumeDetect || group.UsesPhysics)
                    continue;
                foreach (SceneObjectPart part in group.Parts)
                {
                    if (part == null || part.VolumeDetectActive ||
                        (part.Flags & PrimFlags.Phantom) != 0 ||
                        part.PhysicsShapeType == (byte)PhysicsShapeType.None)
                        continue;
                    if (bounds.Count >= maxPrims)
                        throw new InvalidOperationException(
                            "Too many static collidable prims for bounded pathfinding snapshot.");

                    // An oriented prim/mesh contributes its conservative
                    // world-space AABB. Complex mesh interiors remain blocked:
                    // this is a collision projection, not a triangle NavMesh.
                    Vector3 center = part.GetWorldPosition();
                    Vector3 scale = part.Scale;
                    Quaternion rotation = part.GetWorldRotation();
                    if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
                        !float.IsFinite(center.Z) || !float.IsFinite(scale.X) ||
                        !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z) ||
                        scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
                        throw new InvalidOperationException(
                            "Nonfinite static collidable prim encountered.");

                    Vector3 half = scale * 0.5f;
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                    for (int xi = -1; xi <= 1; xi += 2)
                    for (int yi = -1; yi <= 1; yi += 2)
                    for (int zi = -1; zi <= 1; zi += 2)
                    {
                        Vector3 pt = center +
                            new Vector3(xi * half.X, yi * half.Y, zi * half.Z) * rotation;
                        minX = Math.Min(minX, pt.X);
                        minY = Math.Min(minY, pt.Y);
                        minZ = Math.Min(minZ, pt.Z);
                        maxX = Math.Max(maxX, pt.X);
                        maxY = Math.Max(maxY, pt.Y);
                        maxZ = Math.Max(maxZ, pt.Z);
                    }
                    bounds.Add(new OglStaticCollisionAabb(
                        minX, minY, minZ, maxX, maxY, maxZ));
                }
            }
            return bounds;
        }

        private void RefreshDirty()
        {
            if (Volatile.Read(ref m_Closed) != 0)
                return;
            foreach (RegionNavigation state in m_Regions.Values)
            {
                // An epoch mismatch can remain after a terrain event races
                // the final Dirty=0 publication. Always schedule a rebuild
                // for such invalid snapshots, even if Dirty was cleared.
                if (!state.IsNavigationDirty)
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

                // Keep navigation non-ready throughout the entire build.
                // The previous code cleared Dirty before MakeCopy/Build, which
                // permitted a previously published snapshot to be reused.
                int buildEpoch = Volatile.Read(ref state.Epoch);
                Volatile.Write(ref state.Dirty, 1);
                ITerrainChannel copy = state.Scene.Heightmap.MakeCopy();
                int width = checked((int)state.Scene.RegionInfo.RegionSizeX);
                int height = checked((int)state.Scene.RegionInfo.RegionSizeY);
                if (copy == null || copy.Width != width || copy.Height != height)
                    throw new InvalidOperationException("Terrain channel dimensions differ from region dimensions");

                float water = (float)state.Scene.RegionInfo.RegionSettings.WaterHeight;
                bool[] staticBlocked = null;
                if (m_TrackStaticColliders)
                {
                    IReadOnlyList<OglStaticCollisionAabb> colliders =
                        CaptureStaticColliders(state.Scene, m_MaxStaticPrims);
                    staticBlocked = OglTerrainStaticObstacles.Project(
                        width, height, m_CellMeters, m_StaticAgentHeight,
                        (x, y) => copy[x, y], colliders);
                }
                int gridWidth = (width + m_CellMeters - 1) / m_CellMeters;
                OglTerrainNavigationSnapshot snapshot = OglTerrainNavigationSnapshot.Build(
                    width, height, m_CellMeters, water, m_MaxSlope, (x, y) => copy[x, y],
                    staticBlocked == null ? null :
                        (x, y) => staticBlocked[(y / m_CellMeters) * gridWidth +
                                               x / m_CellMeters]);
                OglLayeredNavGraph layered = null;
                if (m_UseLayeredSurfaces)
                {
                    // An opt-in without a verified physics source must fail
                    // closed; never treat AABB projections as extra floors.
                    IOglVerifiedLayeredSurfaceSource provider =
                        state.Scene.RequestModuleInterface<IOglVerifiedLayeredSurfaceSource>();
                    layered = provider?.CaptureVerifiedGraph(
                        state.Scene, m_CellMeters, m_LayeredMaxStep);
                    if (layered == null || layered.CellMeters != m_CellMeters)
                        throw new InvalidOperationException(
                            "Verified layered scene provider missing or returned incompatible graph");
                    // Reject coordinates outside this region even if a
                    // registered provider accidentally returns bad data.
                    for (int i = 0; i < layered.NodeCount; ++i)
                    {
                        OglLayerNavNode node = layered.GetNode(i);
                        if ((node.X + 0.5f) * m_CellMeters >= width ||
                            (node.Y + 0.5f) * m_CellMeters >= height)
                            throw new InvalidOperationException(
                                "Verified layered source contains out-of-region cells");
                    }
                }
                // Invalidation can arrive while building. In that case the
                // freshly built candidate remains unavailable until next refresh.
                Volatile.Write(ref state.Snapshot, snapshot);
                Volatile.Write(ref state.LayeredGraph, layered);
                Volatile.Write(ref state.SnapshotEpoch, buildEpoch);
                // An OnTerrainTainted event racing with this write still
                // increments Epoch; readiness checks compare both epochs.
                if (Volatile.Read(ref state.Epoch) == buildEpoch)
                    Volatile.Write(ref state.Dirty, 0);
                if (Volatile.Read(ref state.Epoch) == buildEpoch &&
                    Volatile.Read(ref state.Dirty) == 0)
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
                if (m_TrackStaticColliders || m_UseLayeredSurfaces)
                {
                    state.Scene.EventManager.OnObjectAddedToScene -= state.StaticAdded;
                    state.Scene.EventManager.OnObjectBeingRemovedFromScene -= state.StaticRemoved;
                    state.Scene.EventManager.OnSceneObjectPartUpdated -= state.StaticUpdated;
                }
                state.Scene.UnregisterModuleInterface<IOglTerrainNavigationRegion>(state);
                state.Scene.UnregisterModuleInterface<IOglNativeTerrainQuery>(state);
            }
            m_Regions.Clear();
        }
    }
}
