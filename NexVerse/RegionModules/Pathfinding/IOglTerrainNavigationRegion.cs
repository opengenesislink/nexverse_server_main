// SPDX-License-Identifier: MPL-2.0
using System.Collections.Generic;
using NexVerse.Core.Pathfinding;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// Revision-safe handoff of a strictly certified immutable navigation
    /// graph to an OpenGenesisLINK-aware viewer transport. This is NOT
    /// the proprietary Firestorm/Havok NavMesh source contract.
    /// </summary>
    public interface IOglCertifiedNavGraphRegion
    {
        bool TryCaptureCertifiedGraph(out OglLayeredNavGraph graph,
            out int revision);
    }

    /// <summary>Internal terrain-route capability; no LSL claims or physics bypass.</summary>
    public interface IOglTerrainNavigationRegion
    {
        bool TryFindTerrainPath(float startX, float startY, float targetX, float targetY,
            out IReadOnlyList<OglNavigationPoint> path);
        bool TryFindNearestTerrainPoint(float x, float y, float z,
            float radius, out OglNavigationPoint point);
        bool IsNavigationReady { get; }
        bool IsNavigationDirty { get; }
    }
}
