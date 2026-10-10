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
                m_MaxSeconds = Math.Clamp(
                    config.GetInt("PhysicsRaycastMaxBuildSeconds", 30), 5, 120);
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
            const int MaxQueries = OglVerifiedSurfaceGraphBuilder.MaxCells * 11;
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

            // No GPU mesh, guessed prim bounding-box floors or inferred
            // vertical connections: only actual physics ray contacts.
            OglLayeredNavGraph graph = OglVerifiedSurfaceGraphBuilder.Build(
                width, height, cellMeters, maxStepMeters, Sample);
            m_Log.InfoFormat(
                "[OGL-PATH]: {0}: {1} verified ray-backed nodes after {2} " +
                "physics queries ({3:0.0}s). Explicit portals not generated.",
                scene.Name, graph.NodeCount, queries, elapsed.Elapsed.TotalSeconds);
            return graph;
        }
    }
}
