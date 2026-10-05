// SPDX-License-Identifier: MPL-2.0

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Optional simulator-level router used by script APIs when a direct
    /// object target is not present in the local region.
    /// </summary>
    public interface ICrossRegionObjectMessageRouter
    {
        bool Enabled { get; }

        bool TryRoute(
            UUID sourceRegionId,
            UUID sourceObjectId,
            UUID sourceOwnerId,
            string sourceName,
            Vector3 sourcePosition,
            UUID targetObjectId,
            int channel,
            string message);
    }
}
