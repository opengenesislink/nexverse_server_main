// SPDX-License-Identifier: MPL-2.0
// Coarse, immutable region heightfield navigation. Not a full 3D navmesh.
using System;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    public readonly struct OglNavigationPoint
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public OglNavigationPoint(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }
    }

    public sealed class OglTerrainNavigationSnapshot
    {
        private readonly OglGridPathfinder m_Routes;
        private readonly float[] m_Heights;
        private readonly int m_RegionWidth;
        private readonly int m_RegionHeight;
        public int CellMeters { get; }
        public int GridWidth => m_Routes.Width;
        public int GridHeight => m_Routes.Height;

        private OglTerrainNavigationSnapshot(
            OglGridPathfinder routes, float[] heights,
            int width, int height, int cellMeters)
        {
            m_Routes = routes;
            m_Heights = heights;
            m_RegionWidth = width;
            m_RegionHeight = height;
            CellMeters = cellMeters;
        }

        /// <summary>
        /// Samples terrain once and builds immutable walkability. Optional
        /// blocked coordinates come from trusted region collision services;
        /// the base heightfield alone cannot detect mesh buildings/doors.
        /// </summary>
        public static OglTerrainNavigationSnapshot Build(
            int regionWidth, int regionHeight, int cellMeters,
            float waterHeight, float maxSlopePerMeter,
            Func<int, int, float> terrainAt,
            Func<int, int, bool> staticObstacleAt = null)
        {
            if (regionWidth <= 0 || regionHeight <= 0 ||
                regionWidth > 4096 || regionHeight > 4096)
                throw new ArgumentOutOfRangeException(nameof(regionWidth));
            if (cellMeters < 1 || cellMeters > 32 ||
                !float.IsFinite(maxSlopePerMeter) || maxSlopePerMeter <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellMeters));
            if (!float.IsFinite(waterHeight))
                throw new ArgumentOutOfRangeException(nameof(waterHeight));
            if (terrainAt == null)
                throw new ArgumentNullException(nameof(terrainAt));

            int width = (regionWidth + cellMeters - 1) / cellMeters;
            int height = (regionHeight + cellMeters - 1) / cellMeters;
            if ((long)width * height > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(cellMeters), "Bounded grid capacity");

            int length = width * height;
            float[] elevations = new float[length];
            bool[] available = new bool[length];

            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int worldX = Math.Min(regionWidth - 1, x * cellMeters + cellMeters / 2);
                    int worldY = Math.Min(regionHeight - 1, y * cellMeters + cellMeters / 2);
                    int index = y * width + x;
                    float elevation = terrainAt(worldX, worldY);
                    elevations[index] = elevation;
                    available[index] = float.IsFinite(elevation) &&
                        elevation > waterHeight &&
                        !(staticObstacleAt?.Invoke(worldX, worldY) ?? false);
                }

            // Conservative slope test: reject cells adjacent to an unsafe
            // 4-neighbour incline. Grid A* additionally blocks corner cutting.
            float riseLimit = maxSlopePerMeter * cellMeters;
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int index = y * width + x;
                    if (!available[index]) continue;
                    float z = elevations[index];
                    if ((x > 0 && Math.Abs(z - elevations[index - 1]) > riseLimit) ||
                        (x + 1 < width && Math.Abs(z - elevations[index + 1]) > riseLimit) ||
                        (y > 0 && Math.Abs(z - elevations[index - width]) > riseLimit) ||
                        (y + 1 < height && Math.Abs(z - elevations[index + width]) > riseLimit))
                        available[index] = false;
                }

            OglGridPathfinder routes = new(width, height,
                (x, y) => available[y * width + x]);
            return new OglTerrainNavigationSnapshot(routes, elevations,
                regionWidth, regionHeight, cellMeters);
        }

        public bool TryFindWorldPath(
            float startX, float startY, float targetX, float targetY,
            out IReadOnlyList<OglNavigationPoint> path,
            int maxExpandedNodes = 20_000)
        {
            path = Array.Empty<OglNavigationPoint>();
            if (!InWorld(startX, startY) || !InWorld(targetX, targetY))
                return false;

            GridCell start = new((int)(startX / CellMeters), (int)(startY / CellMeters));
            GridCell target = new((int)(targetX / CellMeters), (int)(targetY / CellMeters));
            if (!m_Routes.TryFindPath(start, target, out IReadOnlyList<GridCell> route,
                maxExpandedNodes))
                return false;
            List<OglNavigationPoint> points = new(route.Count);
            foreach (GridCell cell in route)
            {
                int x = cell.X * CellMeters + CellMeters / 2;
                int y = cell.Y * CellMeters + CellMeters / 2;
                points.Add(new OglNavigationPoint(
                    Math.Min(m_RegionWidth - 0.5f, x),
                    Math.Min(m_RegionHeight - 0.5f, y),
                    m_Heights[cell.Y * GridWidth + cell.X]));
            }
            path = points;
            return true;
        }

        /// <summary>
        /// Return the nearest walkable cell centre on the *terrain* snapshot,
        /// bounded by a caller-supplied 3D radius and a maximum of 64 metres.
        /// This is not a multi-layer or Firestorm/Havok NavMesh query.
        /// </summary>
        public bool TryFindNearestTerrainPoint(float x, float y, float z,
            float radius, out OglNavigationPoint nearest)
        {
            nearest = default;
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                !float.IsFinite(z) || !float.IsFinite(radius) ||
                radius < 0.5f || radius > 64f || !InWorld(x, y))
                return false;

            int cx = (int)(x / CellMeters);
            int cy = (int)(y / CellMeters);
            int reach = (int)Math.Ceiling(radius / CellMeters) + 1;
            int minX = Math.Max(0, cx - reach);
            int maxX = Math.Min(GridWidth - 1, cx + reach);
            int minY = Math.Max(0, cy - reach);
            int maxY = Math.Min(GridHeight - 1, cy + reach);
            double closest = (double)radius * radius;
            bool found = false;
            for (int yy = minY; yy <= maxY; ++yy)
            {
                for (int xx = minX; xx <= maxX; ++xx)
                {
                    if (!m_Routes.IsWalkable(new GridCell(xx, yy)))
                        continue;
                    float worldX = Math.Min(m_RegionWidth - 0.5f,
                        xx * CellMeters + CellMeters / 2.0f);
                    float worldY = Math.Min(m_RegionHeight - 0.5f,
                        yy * CellMeters + CellMeters / 2.0f);
                    float worldZ = m_Heights[yy * GridWidth + xx];
                    double dx = worldX - x;
                    double dy = worldY - y;
                    double dz = worldZ - z;
                    double distance = dx * dx + dy * dy + dz * dz;
                    if (distance <= closest)
                    {
                        closest = distance;
                        nearest = new OglNavigationPoint(worldX, worldY, worldZ);
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// A bounded static terrain route with SL-style failure codes.
        /// The grid has one surface per X/Y; this cannot represent elevated
        /// walkable meshes, dynamic obstacles or multi-layer Havok NavMesh.
        /// </summary>
        /// <summary>
        /// Conservative 2D clearance against all blocked heightfield cells
        /// and region boundaries. Uses exact point-to-tile distance, not
        /// Euclidean centre-to-centre distance. No 3D mesh clearance claim.
        /// </summary>
        private bool CellHasTerrainClearance(GridCell cell, float radius)
        {
            if (!m_Routes.IsWalkable(cell))
                return false;
            float cx = Math.Min(m_RegionWidth - 0.5f,
                cell.X * CellMeters + CellMeters * 0.5f);
            float cy = Math.Min(m_RegionHeight - 0.5f,
                cell.Y * CellMeters + CellMeters * 0.5f);
            if (cx < radius || cy < radius ||
                m_RegionWidth - cx < radius ||
                m_RegionHeight - cy < radius)
                return false;

            int cells = (int)Math.Ceiling((radius + CellMeters * 0.5f) /
                CellMeters);
            double radiusSq = (double)radius * radius;
            int minX = Math.Max(0, cell.X - cells);
            int maxX = Math.Min(GridWidth - 1, cell.X + cells);
            int minY = Math.Max(0, cell.Y - cells);
            int maxY = Math.Min(GridHeight - 1, cell.Y + cells);

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (m_Routes.IsWalkable(new GridCell(x, y)))
                    continue;
                float left = x * CellMeters;
                float right = Math.Min(m_RegionWidth, (x + 1) * CellMeters);
                float bottom = y * CellMeters;
                float top = Math.Min(m_RegionHeight, (y + 1) * CellMeters);
                float dx = Math.Max(0f, Math.Max(left - cx, cx - right));
                float dy = Math.Max(0f, Math.Max(bottom - cy, cy - top));
                if ((double)dx * dx + (double)dy * dy < radiusSq)
                    return false;
            }
            return true;
        }

        public bool TryFindStaticTerrainRoute(
            float startX, float startY, float startZ,
            float endX, float endY, float endZ, float agentRadius,
            out IReadOnlyList<OglNavigationPoint> waypoints, out int status,
            int maxExpanded = 20000)
        {
            waypoints = Array.Empty<OglNavigationPoint>();
            status = 0xF4240;
            if (!float.IsFinite(agentRadius) || agentRadius < 0.125f ||
                agentRadius > 5f || !float.IsFinite(startX) ||
                !float.IsFinite(startY) || !float.IsFinite(startZ) ||
                !float.IsFinite(endX) || !float.IsFinite(endY) ||
                !float.IsFinite(endZ))
                return false;

            if (!TryFindNearestTerrainPoint(startX, startY, startZ, 8f, out _))
            {
                status = 2; // PU_FAILURE_INVALID_START
                return false;
            }
            if (!TryFindNearestTerrainPoint(endX, endY, endZ, 8f, out _))
            {
                status = 3; // PU_FAILURE_INVALID_GOAL
                return false;
            }
            // Cache each clearance result for this bounded request. Large
            // VAR-region scripts never precompute another entire grid.
            Dictionary<int, bool> clearance = new();
            bool HasClearance(GridCell cell)
            {
                int index = cell.Y * GridWidth + cell.X;
                if (!clearance.TryGetValue(index, out bool allowed))
                {
                    allowed = CellHasTerrainClearance(cell, agentRadius);
                    clearance[index] = allowed;
                }
                return allowed;
            }

            if (!InWorld(startX, startY) ||
                !HasClearance(new GridCell((int)(startX / CellMeters),
                    (int)(startY / CellMeters))))
            {
                status = 2;
                return false;
            }
            if (!InWorld(endX, endY) ||
                !HasClearance(new GridCell((int)(endX / CellMeters),
                    (int)(endY / CellMeters))))
            {
                status = 3;
                return false;
            }

            if (!m_Routes.TryFindPath(
                new GridCell((int)(startX / CellMeters), (int)(startY / CellMeters)),
                new GridCell((int)(endX / CellMeters), (int)(endY / CellMeters)),
                out IReadOnlyList<GridCell> route, maxExpanded, HasClearance))
            {
                status = 4; // PU_FAILURE_UNREACHABLE
                return false;
            }
            if (route.Count == 0 || route.Count > 256)
            {
                status = 0xF4240;
                return false;
            }
            List<OglNavigationPoint> result = new(route.Count);
            foreach (GridCell cell in route)
            {
                result.Add(new OglNavigationPoint(
                    Math.Min(m_RegionWidth - 0.5f,
                        cell.X * CellMeters + CellMeters * 0.5f),
                    Math.Min(m_RegionHeight - 0.5f,
                        cell.Y * CellMeters + CellMeters * 0.5f),
                    m_Heights[cell.Y * GridWidth + cell.X]));
            }
            waypoints = result;
            status = 0;
            return true;
        }

        private bool InWorld(float x, float y) =>
            float.IsFinite(x) && float.IsFinite(y) &&
            x >= 0 && x < m_RegionWidth && y >= 0 && y < m_RegionHeight;
    }
}
