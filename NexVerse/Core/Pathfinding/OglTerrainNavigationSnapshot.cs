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

        private bool InWorld(float x, float y) =>
            float.IsFinite(x) && float.IsFinite(y) &&
            x >= 0 && x < m_RegionWidth && y >= 0 && y < m_RegionHeight;
    }
}
