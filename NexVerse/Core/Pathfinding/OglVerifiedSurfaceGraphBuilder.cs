// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>
    /// A trustworthy, collision-engine supplied downward ray hit.
    /// SourceId=0 is terrain, any other value is a verified static prim
    /// physics actor. It must NOT be inferred from visual bounding boxes.
    /// </summary>
    public readonly struct OglVerifiedSurfaceContact
    {
        public readonly uint SourceId;
        public readonly float Height;
        public readonly float NormalZ;

        public OglVerifiedSurfaceContact(uint sourceId, float height, float normalZ)
        {
            SourceId = sourceId;
            Height = height;
            NormalZ = normalZ;
        }
    }

    /// <summary>
    /// Experimental, fail-closed collision surface sampler for verified
    /// navigation with optional strictly measured lateral transitions.
    /// Five ray samples confirm each local patch; an additional shared
    /// border sample validates each cardinal edge. Stair/ramp adjacency
    /// is opt-in and needs proven contact on both sides plus a separate
    /// agent clearance test; no arbitrary vertical teleport links.
    /// Ray counts are bounded.
    /// This is NOT a Havok or Detour polygon NavMesh.
    /// </summary>
    public static class OglVerifiedSurfaceGraphBuilder
    {
        public const int MaxCells = 4096;
        public const int MaxHitsPerRay = 16;
        private const int MaxLayers = 3;

        private readonly struct VerifiedNode
        {
            public readonly int Index;
            public readonly uint SourceId;
            public readonly float Height;
            public VerifiedNode(int index, uint sourceId, float height)
            {
                Index = index;
                SourceId = sourceId;
                Height = height;
            }
        }

        /// <summary>
        /// Calls sample(x,y) only for in-region points. Null is treated as
        /// physics query failure; empty means verified no hits. The adapter
        /// must independently validate IDs, static flags, normals and ray
        /// hit locations, and MUST impose its own wall-clock deadline.
        /// </summary>
        public static OglLayeredNavGraph Build(
            int width, int height, int cellMeters, float maxStepMeters,
            Func<float, float, IReadOnlyList<OglVerifiedSurfaceContact>> sample,
            float agentRadius = 0.5f, float maxSlopeNormalZ = 0.75f,
            Func<OglClearancePoint, OglClearancePoint, bool> clearCorridor = null,
            bool verifiedTransitions = false)
        {
            if ((verifiedTransitions && clearCorridor == null) ||
                sample == null || width <= 0 || height <= 0 ||
                cellMeters < 1 || cellMeters > 16 ||
                !float.IsFinite(maxStepMeters) || maxStepMeters <= 0 ||
                maxStepMeters > 1.5f ||
                !float.IsFinite(agentRadius) || agentRadius < 0.125f ||
                agentRadius > 0.6f ||
                !float.IsFinite(maxSlopeNormalZ) ||
                maxSlopeNormalZ < 0.5f || maxSlopeNormalZ > 1f)
                throw new ArgumentOutOfRangeException(nameof(sample),
                    "Invalid bounded verified ray sampler configuration.");

            int w = (width + cellMeters - 1) / cellMeters;
            int h = (height + cellMeters - 1) / cellMeters;
            if ((long)w * h > MaxCells)
                throw new ArgumentOutOfRangeException(nameof(width),
                    "Region exceeds the current bounded physics sampling budget.");

            float tolerance = Math.Min(maxStepMeters, 0.5f);
            float halfPatch = Math.Max(agentRadius, 0.25f);
            List<OglLayerNavNode> nodes = new();
            Dictionary<(int x,int y,int layer), VerifiedNode> lookup = new();
            int rays = 0;
            int rayBudget = checked(w * h * (1 + 18 * MaxLayers));

            IReadOnlyList<OglVerifiedSurfaceContact> Ray(float x, float y)
            {
                if (++rays > rayBudget || x < 0f || x >= width ||
                    y < 0f || y >= height)
                    throw new InvalidOperationException(
                        "Verified collision ray budget or region bounds exceeded.");
                IReadOnlyList<OglVerifiedSurfaceContact> hits = sample(x, y);
                if (hits == null || hits.Count >= MaxHitsPerRay)
                    throw new InvalidOperationException(
                        "Physics raycast unavailable, invalid or saturated.");
                foreach (var hit in hits)
                    if (!float.IsFinite(hit.Height) ||
                        !float.IsFinite(hit.NormalZ) ||
                        hit.NormalZ < -1f || hit.NormalZ > 1f)
                        throw new InvalidOperationException(
                            "Physics raycast returned invalid collision data.");
                return hits;
            }

            bool HasContact(IReadOnlyList<OglVerifiedSurfaceContact> hits,
                uint sourceId, float height)
            {
                foreach (var hit in hits)
                    if (hit.SourceId == sourceId &&
                        hit.NormalZ >= maxSlopeNormalZ &&
                        Math.Abs(hit.Height - height) <= tolerance)
                        return true;
                return false;
            }

            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                float cx = (x + 0.5f) * cellMeters;
                float cy = (y + 0.5f) * cellMeters;
                if (cx >= width || cy >= height) continue;
                IReadOnlyList<OglVerifiedSurfaceContact> center = Ray(cx, cy);
                List<OglVerifiedSurfaceContact> candidates = new();
                foreach (var hit in center)
                {
                    if (hit.NormalZ < maxSlopeNormalZ) continue;
                    bool duplicate = false;
                    foreach (var existing in candidates)
                        if (existing.SourceId == hit.SourceId &&
                            Math.Abs(existing.Height - hit.Height) <= 0.05f)
                        {
                            duplicate = true;
                            break;
                        }
                    if (!duplicate) candidates.Add(hit);
                }
                candidates.Sort((a, b) => a.Height.CompareTo(b.Height));
                // A partial, truncated set of levels must never be mistaken
                // for a complete walkable column.
                if (candidates.Count > MaxLayers)
                    throw new InvalidOperationException(
                        "Too many walkable collision surfaces in one column.");

                float patch = Math.Min(halfPatch, Math.Min(cx, Math.Min(cy,
                    Math.Min(width - cx, height - cy))) * 0.9f);
                if (patch < agentRadius * 0.9f) continue;
                // Retain layer ordinal even when a candidate is rejected:
                // no accidental floor/bridge identity collapse.
                for (int layer = 0; layer < candidates.Count; ++layer)
                {
                    var hit = candidates[layer];
                    bool verified = true;
                    for (int sx = -1; sx <= 1 && verified; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        if (!HasContact(Ray(cx + sx * patch, cy + sy * patch),
                                hit.SourceId, hit.Height))
                        {
                            verified = false;
                            break;
                        }
                    }
                    if (!verified) continue;
                    if (clearCorridor != null &&
                        !clearCorridor(
                            new OglClearancePoint(cx, cy, hit.Height),
                            new OglClearancePoint(cx, cy, hit.Height)))
                        continue;
                    int index = nodes.Count;
                    nodes.Add(new OglLayerNavNode(x, y, layer, hit.Height,
                        agentRadius, true));
                    lookup[(x, y, layer)] = new VerifiedNode(
                        index, hit.SourceId, hit.Height);
                }
            }

            if (nodes.Count == 0)
                throw new InvalidOperationException(
                    "No collision-backed walkable surfaces confirmed.");

            // Link *only* cardinal neighbours. A source-aware verified
            // midpoint permits gradients of the same physical ramp. Cross-
            // source steps additionally require proof on EACH side of the
            // border; a disconnected hovering prim never creates a portal.
            // Diagonal shortcuts remain disabled in strict graph mode.
            List<OglLayerNavEdge> edges = new();
            List<OglLayerNavPortal> portals = new();
            int transitions = 0;
            int transitionBudget = checked(w * h * MaxLayers * MaxLayers * 2);
            foreach (var pair in lookup)
            {
                var (x, y, layer) = pair.Key;
                VerifiedNode from = pair.Value;
                foreach (var (dx, dy) in new (int, int)[] { (1, 0), (0, 1) })
                {
                    for (int targetLayer = 0;
                        targetLayer < (verifiedTransitions ? MaxLayers : 1);
                        ++targetLayer)
                    {
                        int candidateLayer = verifiedTransitions ? targetLayer : layer;
                        if (!lookup.TryGetValue((x + dx, y + dy, candidateLayer),
                                out VerifiedNode to) ||
                            (!verifiedTransitions &&
                                from.SourceId != to.SourceId) ||
                            Math.Abs(from.Height - to.Height) > maxStepMeters)
                            continue;
                        if (++transitions > transitionBudget)
                            throw new InvalidOperationException(
                                "Too many candidate physics surface transitions.");
                        float borderX = (x + 0.5f + dx * 0.5f) * cellMeters;
                        float borderY = (y + 0.5f + dy * 0.5f) * cellMeters;
                        if (borderX >= width || borderY >= height) continue;

                        if (from.SourceId == to.SourceId)
                        {
                            if (!HasContact(Ray(borderX, borderY), from.SourceId,
                                    (from.Height + to.Height) * 0.5f))
                                continue;
                        }
                        else
                        {
                            if (!verifiedTransitions) continue;
                            // Both collision actors must reach their own
                            // side of the shared border, without a gap.
                            float inset = Math.Min(0.1f, cellMeters * 0.05f);
                            if (!HasContact(Ray(borderX - dx * inset,
                                                borderY - dy * inset),
                                    from.SourceId, from.Height) ||
                                !HasContact(Ray(borderX + dx * inset,
                                                borderY + dy * inset),
                                    to.SourceId, to.Height))
                                continue;
                        }

                        if (clearCorridor != null &&
                            !clearCorridor(
                                new OglClearancePoint(
                                    (x + 0.5f) * cellMeters,
                                    (y + 0.5f) * cellMeters, from.Height),
                                new OglClearancePoint(
                                    (x + dx + 0.5f) * cellMeters,
                                    (y + dy + 0.5f) * cellMeters, to.Height)))
                            continue;

                        if (layer == candidateLayer)
                            edges.Add(new OglLayerNavEdge(from.Index, to.Index));
                        else
                            portals.Add(new OglLayerNavPortal(
                                from.Index, to.Index, true));
                    }
                }
            }

            return new OglLayeredNavGraph(nodes, portals,
                cellMeters, maxStepMeters, edges);
        }
    }
}
