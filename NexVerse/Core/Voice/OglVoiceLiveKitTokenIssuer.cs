// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexVerse.Core.Voice
{
    /// <summary>
    /// A verified simulator admission assertion. Construct this ONLY after
    /// checking the active root ScenePresence, circuit session, parcel/estate
    /// voice permissions, and foreign HG circuit identity. This DTO alone
    /// does not prove any of those conditions.
    /// </summary>
    public sealed class OglVoiceAdmission
    {
        public string TenantId { get; init; }
        public Guid RegionId { get; init; }
        public Guid AvatarId { get; init; }
        public Guid SessionId { get; init; }
        public string HomeGridOrigin { get; init; }
        public bool IsHypergridGuest { get; init; }
        public bool VoiceAllowed { get; init; }
    }

    public sealed class OglVoiceIssuedToken
    {
        public string Token { get; init; }
        public string Room { get; init; }
        public string ParticipantIdentity { get; init; }
        public long ExpiresAtUnix { get; init; }
    }

    /// <summary>
    /// Standard LiveKit JWT (HS256) with least-privilege video grants.
    /// OGLVoice MUST issue these from the trusted central control plane;
    /// the LiveKit API secret NEVER belongs in simulator settings or CAPS.
    /// </summary>
    public sealed class OglVoiceLiveKitTokenIssuer
    {
        private static readonly Regex s_TenantId = new(
            @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,95}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly string m_ApiKey;
        private readonly byte[] m_ApiSecret;
        private readonly string m_TenantId;

        public OglVoiceLiveKitTokenIssuer(string tenantId, string apiKey, string apiSecret)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || !s_TenantId.IsMatch(tenantId))
                throw new ArgumentException("Tenant ID is invalid", nameof(tenantId));
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 128 ||
                !Regex.IsMatch(apiKey, @"^[a-zA-Z0-9_-]+$"))
                throw new ArgumentException("Invalid LiveKit API key", nameof(apiKey));
            if (string.IsNullOrWhiteSpace(apiSecret) ||
                Encoding.UTF8.GetByteCount(apiSecret) < 32)
                throw new ArgumentException("LiveKit API secret must be at least 32 bytes", nameof(apiSecret));
            m_ApiKey = apiKey;
            m_ApiSecret = Encoding.UTF8.GetBytes(apiSecret);
            m_TenantId = tenantId;
        }

        public OglVoiceIssuedToken Issue(OglVoiceAdmission admission,
            DateTimeOffset now, int lifetimeSeconds = 120)
        {
            if (admission == null)
                throw new ArgumentNullException(nameof(admission));
            if (lifetimeSeconds < 30 || lifetimeSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
            if (admission.TenantId != m_TenantId || !admission.VoiceAllowed ||
                admission.RegionId == Guid.Empty || admission.AvatarId == Guid.Empty ||
                admission.SessionId == Guid.Empty)
                throw new UnauthorizedAccessException("Invalid or unauthorized voice admission");

            string avatarIdentity;
            if (admission.IsHypergridGuest)
            {
                if (string.IsNullOrWhiteSpace(admission.HomeGridOrigin))
                    throw new UnauthorizedAccessException("HG guest must have verified home grid");
                avatarIdentity = OglVoiceHypergridIdentity.DeriveGuestId(
                    admission.HomeGridOrigin, admission.AvatarId);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(admission.HomeGridOrigin))
                    throw new UnauthorizedAccessException("Local identity cannot carry arbitrary HG home");
                avatarIdentity = "local:" + admission.AvatarId.ToString("D");
            }

            // Each participant is unique within its specific region and agent
            // session, including simultaneous HG visitors with colliding UUIDs.
            string room = "ogl." + m_TenantId + ".region." +
                admission.RegionId.ToString("N");
            string identityData = m_TenantId + "\n" + room + "\n" +
                avatarIdentity + "\n" + admission.SessionId.ToString("N");
            string identity = "ogl:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identityData))).ToLowerInvariant();
            long issued = now.ToUnixTimeSeconds();
            long expiry = checked(issued + lifetimeSeconds);

            string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
                new { alg = "HS256", typ = "JWT" }));
            string claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = m_ApiKey,
                sub = identity,
                iat = issued,
                nbf = issued - 5,
                exp = expiry,
                jti = Guid.NewGuid().ToString("N"),
                video = new
                {
                    roomJoin = true,
                    room,
                    canPublish = true,
                    canPublishData = true,
                    canSubscribe = true,
                    canUpdateOwnMetadata = false
                },
                metadata = JsonSerializer.Serialize(new
                {
                    tenant = m_TenantId,
                    region = admission.RegionId.ToString("D"),
                    guest = admission.IsHypergridGuest
                })
            }));
            string unsigned = header + "." + claims;
            byte[] signature = HMACSHA256.HashData(m_ApiSecret, Encoding.ASCII.GetBytes(unsigned));
            return new OglVoiceIssuedToken
            {
                Token = unsigned + "." + Base64Url(signature),
                Room = room,
                ParticipantIdentity = identity,
                ExpiresAtUnix = expiry
            };
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
