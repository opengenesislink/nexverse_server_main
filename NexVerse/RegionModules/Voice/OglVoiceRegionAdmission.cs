// SPDX-License-Identifier: MPL-2.0
using System;
using NexVerse.Core.Voice;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Voice
{
    /// <summary>
    /// Turn the current trusted Region scene into a bounded voice admission
    /// assertion. Never accept an avatar/session/role copied from a viewer form.
    /// This is deliberately separate from provider discovery.
    /// </summary>
    public static class OglVoiceRegionAdmission
    {
        public static bool TryCreate(Scene scene, UUID agentId, string tenant,
            bool allowHypergridGuests, out OglVoiceAdmission admission)
        {
            admission = null;
            if (scene == null || string.IsNullOrWhiteSpace(tenant) ||
                !scene.TryGetScenePresence(agentId, out ScenePresence sp) ||
                sp == null || sp.IsDeleted || sp.IsChildAgent ||
                sp.IsNPC || sp.IsInTransit ||
                sp.ControllingClient == null ||
                scene.RegionInfo?.EstateSettings?.AllowVoice != true)
                return false;

            var circuit = scene.AuthenticateHandler.GetAgentCircuitData(agentId);
            if (circuit == null || circuit.AgentID != agentId ||
                circuit.SessionID != sp.ControllingClient.SessionId ||
                circuit.SessionID.IsZero())
                return false;

            var parcel = scene.LandChannel?.GetLandObject(
                sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
            if (parcel?.LandData == null ||
                (parcel.LandData.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                return false;

            bool guest = (circuit.teleportFlags &
                (uint)Constants.TeleportFlags.ViaHGLogin) != 0;
            if (guest && !allowHypergridGuests)
                return false;

            string home = null;
            if (guest)
            {
                if (circuit.ServiceURLs == null ||
                    !circuit.ServiceURLs.TryGetValue("HomeURI", out object rawHome) ||
                    rawHome == null)
                    return false;
                home = rawHome.ToString();
                // Identity URI is validated again by the central issuer.
                if (!OglVoiceDiscoveryProof.IsSafeServiceUri(home))
                    return false;
            }

            admission = new OglVoiceAdmission
            {
                TenantId = tenant,
                RegionId = Guid.Parse(scene.RegionInfo.RegionID.ToString()),
                AvatarId = Guid.Parse(agentId.ToString()),
                SessionId = Guid.Parse(circuit.SessionID.ToString()),
                IsHypergridGuest = guest,
                HomeGridOrigin = home,
                VoiceAllowed = true
            };
            return true;
        }
    }
}
