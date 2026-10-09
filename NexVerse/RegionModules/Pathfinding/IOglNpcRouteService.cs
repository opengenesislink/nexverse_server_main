// SPDX-License-Identifier: MPL-2.0
using OpenMetaverse;

namespace NexVerse.RegionModules.Pathfinding
{
    /// <summary>
    /// Internal trusted-region NPC navigation API. Does not register any new
    /// LSL/OSSL methods or expose a public admin HTTP endpoint.
    /// </summary>
    public interface IOglNpcRouteService
    {
        bool TryNavigate(UUID npcId, UUID callerId, Vector3 destination,
            out string reason);
        bool Stop(UUID npcId, UUID callerId);
        int ActiveRouteCount { get; }
    }
}
