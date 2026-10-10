// SPDX-License-Identifier: MPL-2.0
//
// OpenGenesisLINK deterministic grid A* foundation. This is a navigation core,
// not a region NavMesh, physics collision solution or viewer LSL integration.
using System;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    public readonly struct GridCell : IEquatable<GridCell>
    {
        public int X { get; }
        public int Y { get; }

        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridCell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridCell other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>
    /// An immutable per-region 2D traversability snapshot. It does not directly
    /// trust user-supplied scripts and does not touch mutable Scene state while
    /// searching. Callers must build/rebuild snapshots when terrain or static
    /// obstacles change, and must revalidate dynamic motion against physics.
    /// </summary>
    public sealed class OglGridPathfinder
    {
        private const int StraightCost = 10;
        private const int DiagonalCost = 14;
        private const int MaxGridCells = 1024 * 1024;
        private readonly bool[] m_walkable;
        private readonly int m_width;
        private readonly int m_height;

        public OglGridPathfinder(int width, int height, Func<int, int, bool> isWalkable)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxGridCells)
                throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions exceed bounded limits");
            if (isWalkable == null)
                throw new ArgumentNullException(nameof(isWalkable));

            m_width = width;
            m_height = height;
            m_walkable = new bool[width * height];
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                    m_walkable[Offset(x, y)] = isWalkable(x, y);
        }

        public int Width => m_width;
        public int Height => m_height;

        public bool IsWalkable(GridCell cell) =>
            InBounds(cell.X, cell.Y) && m_walkable[Offset(cell.X, cell.Y)];

        public bool TryFindPath(
            GridCell start,
            GridCell target,
            out IReadOnlyList<GridCell> path,
            int maxExpandedNodes = 100_000)
        {
            return TryFindPath(start, target, out path, maxExpandedNodes, null);
        }

        /// <summary>
        /// Query-specific extra walkability predicate. This may enforce an
        /// agent's collision radius without changing the immutable grid for
        /// ordinary region/NPC routes. The same predicate is used for
        /// diagonals and endpoints to prevent squeezing through corners.
        /// </summary>
        public bool TryFindPath(
            GridCell start,
            GridCell target,
            out IReadOnlyList<GridCell> path,
            int maxExpandedNodes,
            Func<GridCell, bool> additionalWalkability)
        {
            path = Array.Empty<GridCell>();
            if (maxExpandedNodes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExpandedNodes));

            bool CanWalk(GridCell c) =>
                IsWalkable(c) && (additionalWalkability == null ||
                    additionalWalkability(c));

            if (!CanWalk(start) || !CanWalk(target))
                return false;

            if (start.Equals(target))
            {
                path = new[] { start };
                return true;
            }

            int cells = m_walkable.Length;
            int[] gScores = new int[cells];
            int[] parents = new int[cells];
            bool[] closed = new bool[cells];
            Array.Fill(gScores, int.MaxValue);
            Array.Fill(parents, -1);

            int first = Offset(start.X, start.Y);
            int last = Offset(target.X, target.Y);
            gScores[first] = 0;
            long sequence = 0;
            PriorityQueue<int, (long fScore, long heuristic, long order)> queue = new();
            int hStart = Heuristic(start.X, start.Y, target.X, target.Y);
            queue.Enqueue(first, (hStart, hStart, sequence++));

            int expanded = 0;
            while (queue.TryDequeue(out int current, out _))
            {
                if (closed[current])
                    continue;
                // Hard computation bound protects the simulator from large requests.
                if (++expanded > maxExpandedNodes)
                    return false;

                if (current == last)
                {
                    List<GridCell> reverse = new();
                    for (int at = last; at >= 0; at = parents[at])
                        reverse.Add(new GridCell(at % m_width, at / m_width));
                    reverse.Reverse();
                    path = reverse;
                    return true;
                }

                closed[current] = true;
                int x = current % m_width;
                int y = current / m_width;

                for (int dy = -1; dy <= 1; ++dy)
                {
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = x + dx, ny = y + dy;
                        if (!CanWalk(new GridCell(nx, ny)))
                            continue;

                        // Prevent diagonal corner clipping between blocked tiles.
                        bool diagonal = dx != 0 && dy != 0;
                        if (diagonal &&
                            (!CanWalk(new GridCell(x + dx, y)) ||
                             !CanWalk(new GridCell(x, y + dy))))
                            continue;

                        int neighbor = Offset(nx, ny);
                        if (closed[neighbor])
                            continue;
                        int candidate = gScores[current] + (diagonal ? DiagonalCost : StraightCost);
                        if (candidate >= gScores[neighbor])
                            continue;
                        gScores[neighbor] = candidate;
                        parents[neighbor] = current;
                        int h = Heuristic(nx, ny, target.X, target.Y);
                        queue.Enqueue(neighbor, ((long)candidate + h, h, sequence++));
                    }
                }
            }
            return false;
        }

        private bool InBounds(int x, int y) =>
            (uint)x < (uint)m_width && (uint)y < (uint)m_height;

        private bool IsWalkableInternal(int x, int y) =>
            InBounds(x, y) && m_walkable[Offset(x, y)];

        private int Offset(int x, int y) => y * m_width + x;

        // Admissible octile-distance heuristic for 8-directional movement.
        private static int Heuristic(int x, int y, int goalX, int goalY)
        {
            int dx = Math.Abs(goalX - x);
            int dy = Math.Abs(goalY - y);
            int min = Math.Min(dx, dy);
            return DiagonalCost * min + StraightCost * (Math.Max(dx, dy) - min);
        }
    }
}
