// SPDX-License-Identifier: MPL-2.0
using NexVerse.Core.Voice;
using OpenMetaverse;

namespace NexVerse.RegionModules.Voice
{
    /// <summary>
    /// Local simulator-side admission verification only; does not mint or
    /// transmit LiveKit tokens or provide Firestorm voice capabilities.
    /// </summary>
    public interface IOglVoiceSessionAdmission
    {
        bool TryBuildAdmission(UUID agentId, out OglVoiceAdmission assertion);
    }
}
