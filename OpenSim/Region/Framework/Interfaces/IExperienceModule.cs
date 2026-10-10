// SPDX-License-Identifier: BSD-3-Clause

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Region-facing adapter for the authoritative OpenGenesisLINK Experience service.
    /// Implementations must not keep an independent money-/permission-like authority
    /// in the simulator; they proxy the central service.
    /// </summary>
    public interface IExperienceModule
    {
        UUID ResolveExperience(UUID scriptItemId);

        bool TryGetExperienceDetails(
            UUID experienceId,
            out string name,
            out UUID ownerId,
            out UUID groupId,
            out int maturity,
            out bool enabled);

        bool HasExperiencePermission(
            UUID scriptItemId,
            UUID residentId,
            UUID parcelId,
            out UUID experienceId,
            out string reason);

        /// <summary>
        /// Opt-in, fail-closed pending Experience consent. Only unblocked,
        /// ungranted residents with a live viewer can be queued.
        /// Callback: 0=granted, 4=denied, 18=timeout. Implementations must
        /// revalidate the script, region and parcel before granting.
        /// </summary>
        bool QueueExperiencePermissionRequest(
            UUID scriptItemId,
            UUID objectId,
            UUID residentId,
            UUID parcelId,
            System.Action<int> onResult);

        bool AgentInExperience(
            UUID scriptItemId,
            UUID residentId);

        bool CreateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            out string error);

        bool ReadKeyValue(
            UUID scriptItemId,
            string key,
            out string value,
            out string error);

        bool UpdateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            out bool retryMismatch,
            out string error);

        bool DeleteKeyValue(
            UUID scriptItemId,
            string key,
            out string error);

        bool GetKeyValueStats(
            UUID scriptItemId,
            out long usedBytes,
            out long quotaBytes,
            out int keyCount,
            out string error);

        bool ListKeyValueKeys(
            UUID scriptItemId,
            int start,
            int count,
            out string[] keys,
            out string error);
    }
}
