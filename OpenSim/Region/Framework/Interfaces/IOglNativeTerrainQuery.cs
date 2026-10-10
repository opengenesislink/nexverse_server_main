// SPDX-License-Identifier: MPL-2.0
using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Opt-in native terrain-only navigation query for script API use.
    /// Implementations must reject invalid or stale snapshots. Does not
    /// imply a Firestorm/Havok multi-layer NavMesh is available.
    /// </summary>
    public interface IOglNativeTerrainQuery
    {
        bool TryGetClosestNavPoint(float x, float y, float z,
            float radius, out Vector3 nearest);
    }
}
