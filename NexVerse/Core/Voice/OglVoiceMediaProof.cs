// SPDX-License-Identifier: MPL-2.0
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NexVerse.Core.Voice
{
    /// <summary>
    /// Separate domain for Firestorm SDP/ICE media gateway requests.
    /// SDP offers and batched ICE can be larger than the 4KiB token-minting
    /// request protocol. The receiving gateway MUST bind known NodeIds,
    /// reject replayed nonces and enforce the same timestamp window.
    /// </summary>
    public static class OglVoiceMediaProof
    {
        public const int MaximumPayloadBytes = 64 * 1024;
        private static readonly Regex s_Node = new(
            @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);
        private static readonly Regex s_Nonce = new(@"^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex s_Signature = new(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

        public static string Sign(string key, string node, long stamp, string nonce, byte[] body)
        {
            OglVoiceDiscoveryProof.ValidateKey(key);
            if (string.IsNullOrEmpty(node) || !s_Node.IsMatch(node) ||
                string.IsNullOrEmpty(nonce) || !s_Nonce.IsMatch(nonce) ||
                stamp <= 0 || body == null || body.Length == 0 ||
                body.Length > MaximumPayloadBytes)
                throw new ArgumentException("Invalid or oversized media proof");
            string digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            string message = "oglvoice-media-v1\n" + node + "\n" +
                stamp.ToString(CultureInfo.InvariantCulture) + "\n" + nonce + "\n" + digest;
            return Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        }

        public static bool Verify(string key, string node, string timestamp, string nonce,
            byte[] body, string signature, DateTimeOffset now)
        {
            if (!long.TryParse(timestamp, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long stamp) ||
                Math.Abs(now.ToUnixTimeSeconds() - stamp) > 60 ||
                string.IsNullOrEmpty(signature) || !s_Signature.IsMatch(signature))
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
