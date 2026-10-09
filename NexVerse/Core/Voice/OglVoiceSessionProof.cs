// SPDX-License-Identifier: MPL-2.0
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NexVerse.Core.Voice
{
    /// <summary>Request authentication for private OGLVoice session minting.</summary>
    public static class OglVoiceSessionProof
    {
        private static readonly Regex s_Node = new(
            @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
        private static readonly Regex s_Nonce = new(@"^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex s_Signature = new(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

        public static string Sign(string key, string node, long stamp,
            string nonce, byte[] body)
        {
            OglVoiceDiscoveryProof.ValidateKey(key);
            if (node == null || !s_Node.IsMatch(node) ||
                nonce == null || !s_Nonce.IsMatch(nonce) ||
                stamp <= 0 || body == null || body.Length == 0 || body.Length > 4096)
                throw new ArgumentException("Invalid OGLVoice session proof input");

            string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            string message = "session\n" + node + "\n" +
                stamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                nonce + "\n" + bodyHash;
            return Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message))
            ).ToLowerInvariant();
        }

        public static bool Verify(string key, string node, string timestamp, string nonce,
            byte[] body, string signature, DateTimeOffset now)
        {
            if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture,
                    out long stamp) ||
                Math.Abs(now.ToUnixTimeSeconds() - stamp) > 60 ||
                signature == null || !s_Signature.IsMatch(signature))
                return false;
            try
            {
                byte[] expected = Convert.FromHexString(Sign(key, node, stamp, nonce, body));
                return CryptographicOperations.FixedTimeEquals(
                    expected, Convert.FromHexString(signature));
            }
            catch (ArgumentException) { return false; }
        }
    }
}
