// SPDX-License-Identifier: MPL-2.0
using System.Collections.Generic;
using NexVerse.Core.Pathfinding;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>Internal terrain-route capability; no LSL claims or physics bypass.</summary>
    public interface IOglTerrainNavigationRegion
    {
        bool TryFindTerrainPath(float startX, float startY, float targetX, float targetY,
            out IReadOnlyList<OglNavigationPoint> path);
        bool IsNavigationReady { get; }
        bool IsNavigationDirty { get; }
    }
}
