// SPDX-License-Identifier: MPL-2.0
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NexVerse.Core.Voice
{
    // Safe *public provider metadata*. Never include API, LiveKit or billing secrets.
    public sealed class OglVoiceProviderDescriptor
    {
        public const string Protocol = "oglvoice-discovery-v1";
        public string protocol { get; set; } = Protocol;
        public string provider_url { get; set; }
        public string tenant_id { get; set; }
        public bool hypergrid_guests { get; set; }
        public string media_transport { get; set; } = "livekit-webrtc";
        public string viewer_capability { get; set; } = "not-yet-implemented";
        public string media_gateway_url { get; set; }

        public void Validate()
        {
            if (protocol != Protocol || media_transport != "livekit-webrtc")
                throw new ArgumentException("Unsupported OGLVoice provider protocol");
            if (!OglVoiceDiscoveryProof.IsSafeServiceUri(provider_url))
                throw new ArgumentException("OGLVoice provider URL must use HTTPS or local loopback HTTP");
            if (viewer_capability != "not-yet-implemented" &&
                viewer_capability != "firestorm-webrtc-v1")
                throw new ArgumentException("Unsupported OGLVoice viewer bridge contract");
            if (!string.IsNullOrWhiteSpace(media_gateway_url) &&
                (!OglVoiceDiscoveryProof.IsSafeServiceUri(media_gateway_url) ||
                 media_gateway_url.Length > 1024))
                throw new ArgumentException("Invalid OGLVoice media bridge URL");
            if (viewer_capability == "firestorm-webrtc-v1" &&
                string.IsNullOrWhiteSpace(media_gateway_url))
                throw new ArgumentException("WebRTC viewer capability requires an explicit media gateway");
            if (string.IsNullOrEmpty(tenant_id) || tenant_id.Length > 96 ||
                !Regex.IsMatch(tenant_id, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
                throw new ArgumentException("Invalid OGLVoice tenant ID");
        }
    }

    /// <summary>
    /// Shared-key authenticated grid discovery, reusing the already provisioned
    /// NexBus key. No administrator key is sent to a simulator or viewer.
    /// </summary>
    public static class OglVoiceDiscoveryProof
    {
        private static readonly Regex s_NodeName = new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex s_Nonce = new(@"^[a-f0-9]{32}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex s_Hex = new(@"^[a-f0-9]{64}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public static bool IsSafeServiceUri(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.Query))
                return false;
            return uri.Scheme == Uri.UriSchemeHttps ||
                (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        }

        public static string RequestSignature(string key, string node, long unixSeconds, string nonce)
        {
            ValidateKey(key);
            ValidateRequestParts(node, nonce);
            if (unixSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(unixSeconds));
            return Digest(key, "request\n" + node + "\n" + unixSeconds + "\n" + nonce);
        }

        public static bool VerifyRequest(
            string key, string node, string unixSeconds, string nonce, string signature,
            DateTimeOffset now)
        {
            if (!long.TryParse(unixSeconds, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long stamp) ||
                Math.Abs(now.ToUnixTimeSeconds() - stamp) > 60)
                return false;
            try
            {
                return EqualHex(RequestSignature(key, node, stamp, nonce), signature);
            }
            catch (ArgumentException) { return false; }
        }

        public static string ResponseSignature(string key, string nonce, byte[] responseBody)
        {
            ValidateKey(key);
            if (nonce == null || !s_Nonce.IsMatch(nonce) || responseBody == null ||
                responseBody.Length > 8192)
                throw new ArgumentException("Invalid provider discovery response");
            // Bind to both the original challenge nonce and the exact returned bytes.
            byte[] prefix = Encoding.UTF8.GetBytes("response\n" + nonce + "\n");
            byte[] data = new byte[prefix.Length + responseBody.Length];
            Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
            Buffer.BlockCopy(responseBody, 0, data, prefix.Length, responseBody.Length);
            return DigestBytes(key, data);
        }

        public static bool VerifyResponse(string key, string nonce, byte[] bytes, string signature)
        {
            try { return EqualHex(ResponseSignature(key, nonce, bytes), signature); }
            catch (ArgumentException) { return false; }
        }

        public static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) < 32)
                throw new ArgumentException("NexBus shared key must be at least 32 UTF-8 bytes");
        }

        private static void ValidateRequestParts(string node, string nonce)
        {
            if (string.IsNullOrEmpty(node) || !s_NodeName.IsMatch(node) ||
                string.IsNullOrEmpty(nonce) || !s_Nonce.IsMatch(nonce))
                throw new ArgumentException("Invalid NodeId or nonce");
        }

        private static string Digest(string key, string payload) =>
            DigestBytes(key, Encoding.UTF8.GetBytes(payload));

        private static string DigestBytes(string key, byte[] bytes) =>
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), bytes)).ToLowerInvariant();

        private static bool EqualHex(string expected, string actual)
        {
            if (actual == null || !s_Hex.IsMatch(actual))
                return false;
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), Convert.FromHexString(actual));
        }
    }
}
