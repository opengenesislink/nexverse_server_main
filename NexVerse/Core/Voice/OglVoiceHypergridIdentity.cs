// SPDX-License-Identifier: MPL-2.0
using System;
using System.Security.Cryptography;
using System.Text;

namespace NexVerse.Core.Voice
{
    /// <summary>
    /// Distinct voice identity for a foreign HG visitor. The trusted receiving
    /// simulator must first authenticate the actual HG agent session, then use
    /// its verified home URI + ORIGINAL avatar UUID (not a temporary local UUID).
    /// Identity creation alone never authorizes voice-room membership.
    /// </summary>
    public static class OglVoiceHypergridIdentity
    {
        public static string DeriveGuestId(string verifiedHomeUri, Guid originalAvatarId)
        {
            if (originalAvatarId == Guid.Empty)
                throw new ArgumentException("Original avatar UUID must not be empty", nameof(originalAvatarId));
            if (!Uri.TryCreate(verifiedHomeUri, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("HG home grid URI must be absolute HTTP(S) without userinfo/query/fragment");
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path != string.Empty && path != "/")
                throw new ArgumentException("HG voice identity expects canonical home grid origin, not per-user paths");
            string origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            // URI implements lowercasing of host/scheme, canonical default ports.
            string value = origin + "\n" + originalAvatarId.ToString("D");
            return "hg:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
    }
}
