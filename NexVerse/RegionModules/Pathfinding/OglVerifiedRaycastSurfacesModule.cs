// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using log4net;
using Mono.Addins;
using NexVerse.Core.Pathfinding;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// Experimental real-physics collision sampler. Uses filtered, bounded
    /// downward rays to discover walkable terrain/static actor contact
    /// surfaces. This is NOT a triangle extraction or Firestorm NavMesh.
    /// No unsupported PhysicsScene is ever treated as successful.
    /// No stairs, elevators or off-mesh portals are inferred.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglVerifiedRaycastSurfacesModule")]
    public sealed class OglVerifiedRaycastSurfacesModule :
        ISharedRegionModule, IOglVerifiedLayeredSurfaceSource
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(OglVerifiedRaycastSurfacesModule));
        private readonly ConcurrentDictionary<UUID, Scene> m_Regions = new();
        private bool m_Enabled;
        private bool m_MultiRayClearance;
        private bool m_VerifiedTransitions;
        private float m_AgentHeight = 1.8f;
        private float m_AgentRadius = 0.5f;
        private int m_MaxSeconds = 30;

        public string Name => "OGL Verified Physics Surface Sampler (experimental)";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source?.Configs["OGLPathfinding"];
            m_Enabled = config?.GetBoolean("Enabled", false) == true &&
                config.GetBoolean("UseVerifiedLayeredSurfaces", false) &&
                config.GetBoolean("PhysicsRaycastLayeredSurfaces", false);
            if (m_Enabled)
            {
                m_MaxSeconds = Math.Clamp(
                    config.GetInt("PhysicsRaycastMaxBuildSeconds", 30), 5, 120);
                m_MultiRayClearance = config.GetBoolean(
                    "PhysicsMultiRayClearance", false);
                m_VerifiedTransitions = config.GetBoolean(
                    "PhysicsVerifiedTransitions", false);
                m_AgentHeight = Math.Clamp(
                    config.GetFloat("PhysicsAgentHeight", 1.8f), 1f, 3f);
                m_AgentRadius = Math.Clamp(
                    config.GetFloat("PhysicsAgentRadius", 0.5f), 0.125f, 0.6f);
                if (m_VerifiedTransitions && !m_MultiRayClearance)
                    throw new InvalidOperationException(
                        "PhysicsVerifiedTransitions requires PhysicsMultiRayClearance.");
            }
        }

        public void PostInitialise() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null) return;
            if (m_Regions.TryAdd(scene.RegionInfo.RegionID, scene))
            {
                scene.RegisterModuleInterface<IOglVerifiedLayeredSurfaceSource>(this);
                m_Log.InfoFormat(
                    "[OGL-PATH]: Verified collision sampler registered for {0}. " +
                    "Sampling remains bounded and does not provide off-mesh links.",
                    scene.Name);
            }
        }

        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null) return;
            if (m_Regions.TryRemove(scene.RegionInfo.RegionID, out _))
                scene.UnregisterModuleInterface<IOglVerifiedLayeredSurfaceSource>(this);
        }

        public void Close()
        {
            foreach (Scene scene in m_Regions.Values)
                scene.UnregisterModuleInterface<IOglVerifiedLayeredSurfaceSource>(this);
            m_Regions.Clear();
        }

        public OglLayeredNavGraph CaptureVerifiedGraph(
            Scene scene, int cellMeters, float maxStepMeters)
        {
            if (!m_Enabled || scene == null ||
                !m_Regions.TryGetValue(scene.RegionInfo.RegionID, out Scene registered) ||
                !ReferenceEquals(registered, scene) ||
                !scene.SupportsRayCastFiltered())
                throw new InvalidOperationException(
                    "No authorized region or supported filtered physics raycaster.");

            ITerrainChannel terrain = scene.Heightmap?.MakeCopy();
            int width = checked((int)scene.RegionInfo.RegionSizeX);
            int height = checked((int)scene.RegionInfo.RegionSizeY);
            if (terrain == null || terrain.Width != width ||
                terrain.Height != height)
                throw new InvalidOperationException(
                    "Verified sampler requires a stable, correctly-sized terrain snapshot.");
            if (((long)(width + cellMeters - 1) / cellMeters) *
                ((long)(height + cellMeters - 1) / cellMeters) >
                    OglVerifiedSurfaceGraphBuilder.MaxCells)
                throw new InvalidOperationException(
                    "Region sampling would exceed the configured cell budget.");

            Stopwatch elapsed = Stopwatch.StartNew();
            int queries = 0;
            // Includes surface sampling plus bounded multi-ray checks per
            // cell and transition. Exceeding this limit fails the build.
            const int MaxQueries = OglVerifiedSurfaceGraphBuilder.MaxCells * 700;
            IReadOnlyList<OglVerifiedSurfaceContact> Sample(float x, float y)
            {
                if (++queries > MaxQueries || elapsed.Elapsed.TotalSeconds >
                    m_MaxSeconds || x < 0 || y < 0 || x >= width || y >= height)
                    throw new InvalidOperationException(
                        "Verified physics raycast deadline or budget exceeded.");
                // ubOde's terrain ray filter has a hard 60-metre range
                // constraint. This 52m column measures up to 40m above
                // terrain while preserving an 12m terrain-underfoot margin.
                // Unsupported higher structures MUST fail as absent.
                float ground = terrain[(int)x, (int)y];
                if (!float.IsFinite(ground))
                    throw new InvalidOperationException("Nonfinite terrain elevation.");
                Vector3 origin = new(x, y, ground + 40f);
                object result = scene.RayCastFiltered(origin,
                    new Vector3(0f, 0f, -1f), 52f, 16,
                    RayFilterFlags.land | RayFilterFlags.nonphysical);
                if (result is not List<ContactResult> hits)
                    throw new InvalidOperationException(
                        "Physics backend did not return valid filtered collision hits.");
                if (hits.Count >= OglVerifiedSurfaceGraphBuilder.MaxHitsPerRay)
                    throw new InvalidOperationException("Too many physics intersections.");
                List<OglVerifiedSurfaceContact> verified = new(hits.Count);
                foreach (ContactResult hit in hits)
                {
                    if (!float.IsFinite(hit.Pos.X) ||
                        !float.IsFinite(hit.Pos.Y) ||
                        !float.IsFinite(hit.Pos.Z) ||
                        !float.IsFinite(hit.Normal.Z) ||
                        !float.IsFinite(hit.Depth) ||
                        Math.Abs(hit.Pos.X - x) > 0.2f ||
                        Math.Abs(hit.Pos.Y - y) > 0.2f ||
                        hit.Depth < 0f || hit.Depth > 52f)
                        throw new InvalidOperationException(
                            "Malformed collision ray hit from the physics engine.");

                    if (hit.Normal.Z < 0.75f) continue;
                    if (hit.ConsumerID == 0)
                    {
                        // A terrain collision must actually agree with the
                        // terrain snapshot, not a synthetic hit from another
                        // object with ID zero.
                        if (Math.Abs(hit.Pos.Z - ground) > 1.5f)
                            continue;
                    }
                    else
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(hit.ConsumerID);
                        SceneObjectGroup group = part?.ParentGroup;
                        if (part == null || group == null || group.IsDeleted ||
                            group.IsAttachmentCheckFull() || group.UsesPhysics ||
                            group.IsPhantom || group.IsVolumeDetect ||
                            part.VolumeDetectActive ||
                            part.PhysicsShapeType == (byte)PhysicsShapeType.None ||
                            (part.Flags & PrimFlags.Phantom) != 0)
                            continue;
                    }
                    verified.Add(new OglVerifiedSurfaceContact(
                        hit.ConsumerID, hit.Pos.Z, hit.Normal.Z));
                }
                return verified;
            }

            // A grid of rays checks commonly obstructed standing volumes
            // and corridors. This is NOT a true capsule sweep; the physics
            // APIs expose no implemented sphere/box sweep in current engines.
            bool ClearRay(OglClearancePoint start, OglClearancePoint end)
            {
                if (++queries > MaxQueries ||
                    elapsed.Elapsed.TotalSeconds > m_MaxSeconds)
                    throw new InvalidOperationException(
                        "Physics clearance ray deadline or budget exceeded.");
                if (start.X < 0f || start.X >= width ||
                    start.Y < 0f || start.Y >= height ||
                    end.X < 0f || end.X >= width ||
                    end.Y < 0f || end.Y >= height)
                    return false;
                Vector3 delta = new(
                    end.X - start.X, end.Y - start.Y, end.Z - start.Z);
                float length = delta.Length();
                if (!float.IsFinite(length) || length < 0.01f ||
                    length > 16.5f)
                    return false;
                Vector3 direction = delta / length;
                object result = scene.RayCastFiltered(
                    new Vector3(start.X, start.Y, start.Z), direction,
                    length, 16, RayFilterFlags.PrimsNonPhantom);
                if (result is not List<ContactResult> hits ||
                    hits.Count >= OglVerifiedSurfaceGraphBuilder.MaxHitsPerRay)
                    throw new InvalidOperationException(
                        "Physics clearance ray unavailable or saturated.");
                foreach (ContactResult hit in hits)
                {
                    if (!float.IsFinite(hit.Depth) ||
                        !float.IsFinite(hit.Pos.X) ||
                        !float.IsFinite(hit.Pos.Y) ||
                        !float.IsFinite(hit.Pos.Z))
                        throw new InvalidOperationException(
                            "Invalid physics clearance ray hit.");
                }
                return hits.Count == 0;
            }

            bool ClearCorridor(OglClearancePoint from, OglClearancePoint to) =>
                OglMultiRayAgentClearance.IsCorridorClear(
                    from, to, m_AgentRadius, m_AgentHeight, ClearRay);

            Func<OglClearancePoint, OglClearancePoint, bool> clearance =
                m_MultiRayClearance ? ClearCorridor : null;

            // A transition between stair/ramp steps requires separate
            // border contact proofs plus a free-space multi-ray corridor.
            OglLayeredNavGraph graph = OglVerifiedSurfaceGraphBuilder.Build(
                width, height, cellMeters, maxStepMeters, Sample,
                m_AgentRadius, 0.75f, clearance, m_VerifiedTransitions);
            m_Log.InfoFormat(
                "[OGL-PATH]: {0}: {1} verified ray-backed nodes after {2} " +
                "physics queries ({3:0.0}s). Explicit portals not generated.",
                scene.Name, graph.NodeCount, queries, elapsed.Elapsed.TotalSeconds);
            return graph;
        }
    }
}
