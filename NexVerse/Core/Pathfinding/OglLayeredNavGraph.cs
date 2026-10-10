// SPDX-License-Identifier: MPL-2.0
// OpenGenesisLINK: deterministic, bounded multi-surface navigation GRAPH.
// The caller provides real walkable surface samples and verified portals.
// This is not a mesh triangle extractor or Firestorm's binary Havok NavMesh.
using System;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    public readonly struct OglLayerNavNode
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Layer;
        public readonly float Z;
        public readonly float MaxAgentRadius;
        public readonly bool Walkable;

        public OglLayerNavNode(int x, int y, int layer, float z,
            float maxAgentRadius, bool walkable = true)
        {
            X = x;
            Y = y;
            Layer = layer;
            Z = z;
            MaxAgentRadius = maxAgentRadius;
            Walkable = walkable;
        }
    }

    /// <summary>
    /// Explicit connectivity between two sampled surfaces. An explicit
    /// portal represents a verified stair, ramp, ladder or other off-mesh
    /// link. The graph NEVER guesses links between overlapping floors.
    /// </summary>
    public readonly struct OglLayerNavPortal
    {
        public readonly int From;
        public readonly int To;
        public readonly bool Bidirectional;

        public OglLayerNavPortal(int from, int to, bool bidirectional = true)
        {
            From = from;
            To = to;
            Bidirectional = bidirectional;
        }
    }

    /// <summary>
    /// Immutable queryable multi-level navigation graph with radius-aware
    /// nodes and explicit cross-layer links. Neighbour search never reads
    /// mutable Scene/Physics data and is bounded for VAR-region safety.
    /// </summary>
    public sealed class OglLayeredNavGraph
    {
        private const int MaxNodes = 65536;
        private const int MaxPortals = 32768;
        private const int MaxPathNodes = 512;
        private readonly OglLayerNavNode[] m_Nodes;
        private readonly Dictionary<(int x, int y, int layer), int> m_Grid = new();
        private readonly List<int>[] m_Portals;
        private readonly float m_CellMeters;
        private readonly float m_MaxStepMeters;

        public OglLayeredNavGraph(IReadOnlyList<OglLayerNavNode> nodes,
            IReadOnlyList<OglLayerNavPortal> portals,
            float cellMeters, float maxStepMeters)
        {
            if (nodes == null || nodes.Count == 0 || nodes.Count > MaxNodes)
                throw new ArgumentOutOfRangeException(nameof(nodes));
            if (portals == null || portals.Count > MaxPortals)
                throw new ArgumentOutOfRangeException(nameof(portals));
            if (!float.IsFinite(cellMeters) || cellMeters < 0.25f ||
                cellMeters > 32f || !float.IsFinite(maxStepMeters) ||
                maxStepMeters < 0 || maxStepMeters > 3f)
                throw new ArgumentOutOfRangeException(nameof(cellMeters));

            m_CellMeters = cellMeters;
            m_MaxStepMeters = maxStepMeters;
            m_Nodes = new OglLayerNavNode[nodes.Count];
            m_Portals = new List<int>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                OglLayerNavNode node = nodes[i];
                if (node.X < 0 || node.X > 4095 || node.Y < 0 ||
                    node.Y > 4095 || node.Layer < 0 || node.Layer > 1023 ||
                    !float.IsFinite(node.Z) ||
                    !float.IsFinite(node.MaxAgentRadius) ||
                    node.MaxAgentRadius < 0 || node.MaxAgentRadius > 5f ||
                    !m_Grid.TryAdd((node.X, node.Y, node.Layer), i))
                    throw new ArgumentException(
                        "Duplicate, invalid or nonfinite layered navigation surface.");
                m_Nodes[i] = node;
                m_Portals[i] = new List<int>();
            }
            foreach (OglLayerNavPortal portal in portals)
            {
                if ((uint)portal.From >= (uint)m_Nodes.Length ||
                    (uint)portal.To >= (uint)m_Nodes.Length ||
                    portal.From == portal.To)
                    throw new ArgumentException("Invalid navigation portal endpoint.");
                m_Portals[portal.From].Add(portal.To);
                if (portal.Bidirectional)
                    m_Portals[portal.To].Add(portal.From);
            }
        }

        public int NodeCount => m_Nodes.Length;
        public OglLayerNavNode GetNode(int index) => m_Nodes[index];

        private bool Allowed(int index, float radius) =>
            m_Nodes[index].Walkable && m_Nodes[index].MaxAgentRadius >= radius;

        private bool TryNeighbour(int x, int y, int layer, float radius,
            out int index) =>
            m_Grid.TryGetValue((x, y, layer), out index) && Allowed(index, radius);

        private bool StepAllowed(int a, int b, float radius) =>
            Allowed(b, radius) &&
            Math.Abs(m_Nodes[a].Z - m_Nodes[b].Z) <= m_MaxStepMeters;

        private double Distance(int from, int to)
        {
            OglLayerNavNode a = m_Nodes[from], b = m_Nodes[to];
            double dx = (double)(a.X - b.X) * m_CellMeters;
            double dy = (double)(a.Y - b.Y) * m_CellMeters;
            double dz = (double)a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Resolve a 3D world point to a walkable surface using the full
        /// X/Y/Z distance, not X/Y alone. This prevents a bridge query from
        /// silently selecting the ground beneath it. Node scanning is
        /// strictly capped by the constructor's 65,536-node budget.
        /// </summary>
        public bool TryFindClosestSurface(float worldX, float worldY, float worldZ,
            float searchRadius, float agentRadius, out int nodeIndex)
        {
            nodeIndex = -1;
            if (!float.IsFinite(worldX) || !float.IsFinite(worldY) ||
                !float.IsFinite(worldZ) || !float.IsFinite(searchRadius) ||
                searchRadius < 0.5f || searchRadius > 64f ||
                !float.IsFinite(agentRadius) || agentRadius < 0.125f ||
                agentRadius > 5f)
                return false;

            double bestSquared = (double)searchRadius * searchRadius;
            for (int i = 0; i < m_Nodes.Length; ++i)
            {
                if (!Allowed(i, agentRadius)) continue;
                OglLayerNavNode node = m_Nodes[i];
                double dx = (node.X + 0.5d) * m_CellMeters - worldX;
                double dy = (node.Y + 0.5d) * m_CellMeters - worldY;
                double dz = (double)node.Z - worldZ;
                double distanceSquared = dx * dx + dy * dy + dz * dz;
                if (distanceSquared > bestSquared) continue;
                // Deterministic tie-breaking independent of iteration order.
                if (nodeIndex >= 0 && distanceSquared == bestSquared)
                    continue;
                bestSquared = distanceSquared;
                nodeIndex = i;
            }
            return nodeIndex >= 0;
        }

        /// <summary>
        /// Static world-space route for trusted, already-validated scene
        /// surfaces and portals. Returns SL-compatible numeric PU codes.
        /// No source provider means no layered graph and NO implicit fallbacks.
        /// </summary>
        public bool TryFindWorldPath(
            float startX, float startY, float startZ,
            float goalX, float goalY, float goalZ, float agentRadius,
            out IReadOnlyList<OglNavigationPoint> waypoints,
            out int status, int maxExpandedNodes = 20000)
        {
            waypoints = Array.Empty<OglNavigationPoint>();
            status = 0xF4240; // PU_FAILURE_OTHER
            if (!float.IsFinite(agentRadius) || agentRadius < 0.125f ||
                agentRadius > 5f || !float.IsFinite(startX) ||
                !float.IsFinite(startY) || !float.IsFinite(startZ) ||
                !float.IsFinite(goalX) || !float.IsFinite(goalY) ||
                !float.IsFinite(goalZ) || maxExpandedNodes <= 0 ||
                maxExpandedNodes > 100000)
                return false;

            if (!TryFindClosestSurface(startX, startY, startZ,
                    8f, agentRadius, out int from))
            {
                status = 2; // PU_FAILURE_INVALID_START
                return false;
            }
            if (!TryFindClosestSurface(goalX, goalY, goalZ,
                    8f, agentRadius, out int to))
            {
                status = 3; // PU_FAILURE_INVALID_GOAL
                return false;
            }
            if (!TryFindPath(from, to, agentRadius, out IReadOnlyList<int> route,
                    maxExpandedNodes))
            {
                status = 4; // PU_FAILURE_UNREACHABLE
                return false;
            }
            // llGetStaticPath's terrain implementation is limited to
            // 256 waypoints; keep the same externally observable limit.
            if (route.Count == 0 || route.Count > 256)
                return false;

            List<OglNavigationPoint> result = new(route.Count);
            foreach (int index in route)
            {
                OglLayerNavNode node = m_Nodes[index];
                result.Add(new OglNavigationPoint(
                    (node.X + 0.5f) * m_CellMeters,
                    (node.Y + 0.5f) * m_CellMeters,
                    node.Z));
            }
            waypoints = result;
            status = 0;
            return true;
        }

        public bool TryFindPath(int start, int target, float agentRadius,
            out IReadOnlyList<int> path, int maxExpandedNodes = 20000)
        {
            path = Array.Empty<int>();
            if ((uint)start >= (uint)m_Nodes.Length ||
                (uint)target >= (uint)m_Nodes.Length ||
                !float.IsFinite(agentRadius) || agentRadius < 0.125f ||
                agentRadius > 5f || maxExpandedNodes <= 0 ||
                maxExpandedNodes > 100000 ||
                !Allowed(start, agentRadius) || !Allowed(target, agentRadius))
                return false;

            if (start == target)
            {
                path = new[] { start };
                return true;
            }

            double[] g = new double[m_Nodes.Length];
            int[] parent = new int[m_Nodes.Length];
            bool[] closed = new bool[m_Nodes.Length];
            Array.Fill(g, double.PositiveInfinity);
            Array.Fill(parent, -1);
            g[start] = 0;
            long sequence = 0;
            PriorityQueue<int, (double score, long order)> open = new();
            open.Enqueue(start, (Distance(start, target), sequence++));
            int expanded = 0;

            while (open.TryDequeue(out int current, out _))
            {
                if (closed[current]) continue;
                if (++expanded > maxExpandedNodes) return false;
                if (current == target)
                {
                    List<int> reverse = new();
                    for (int at = target; at >= 0; at = parent[at])
                    {
                        reverse.Add(at);
                        if (reverse.Count > MaxPathNodes) return false;
                    }
                    reverse.Reverse();
                    path = reverse;
                    return true;
                }
                closed[current] = true;
                OglLayerNavNode from = m_Nodes[current];
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (!TryNeighbour(from.X + dx, from.Y + dy,
                            from.Layer, agentRadius, out int next) ||
                        !StepAllowed(current, next, agentRadius))
                        continue;

                    if (dx != 0 && dy != 0)
                    {
                        // Prevent diagonal clipping through missing floors,
                        // steep gaps or cells inaccessible to this radius.
                        if (!TryNeighbour(from.X + dx, from.Y,
                                from.Layer, agentRadius, out int sideX) ||
                            !TryNeighbour(from.X, from.Y + dy,
                                from.Layer, agentRadius, out int sideY) ||
                            !StepAllowed(current, sideX, agentRadius) ||
                            !StepAllowed(current, sideY, agentRadius) ||
                            !StepAllowed(sideX, next, agentRadius) ||
                            !StepAllowed(sideY, next, agentRadius))
                            continue;
                    }
                    Relax(next);
                }

                // Portals are the ONLY way to switch to a different layer.
                // The Scene adapter must verify portal geometry and rights.
                foreach (int next in m_Portals[current])
                    if (Allowed(next, agentRadius)) Relax(next);

                void Relax(int next)
                {
                    if (closed[next]) return;
                    double nextCost = g[current] + Distance(current, next);
                    if (nextCost >= g[next]) return;
                    g[next] = nextCost;
                    parent[next] = current;
                    open.Enqueue(next,
                        (nextCost + Distance(next, target), sequence++));
                }
            }
            return false;
        }
    }
}
