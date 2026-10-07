// SPDX-License-Identifier: MPL-2.0

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Optional simulator-level router for estate-wide script chat.
    /// Local same-process delivery remains in the LSL API; this router
    /// extends delivery to matching Estate regions on other simulator nodes.
    /// </summary>
    public interface IEstateScriptMessageRouter
    {
        bool TryRouteEstateMessage(
            UUID sourceRegionId,
            UUID sourceObjectId,
            UUID sourceOwnerId,
            string sourceName,
            uint estateId,
            int channel,
            string message);
    }
}
