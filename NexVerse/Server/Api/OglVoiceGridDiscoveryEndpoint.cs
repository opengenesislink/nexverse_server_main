// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Voice;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Robust-only signed grid provider discovery. This returns metadata, NOT
    /// voice participant tokens, LiveKit credentials, or management privileges.
    /// An authenticated simulator still needs a separate viewer/room adapter.
    /// </summary>
    internal sealed class OglVoiceGridDiscoveryEndpoint
    {
        public const string Route = "/internal/oglvoice/v1/provider";
        private readonly OglVoiceProviderDescriptor m_Provider;
        private readonly string m_Key;
        private readonly object m_SeenLock = new();
        private readonly Dictionary<string, long> m_Seen = new(StringComparer.Ordinal);
        private const int MaxOutstandingChallenges = 4096;

        public OglVoiceGridDiscoveryEndpoint(OglVoiceProviderDescriptor provider, string nexBusKey)
        {
            m_Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            m_Provider.Validate();
            OglVoiceDiscoveryProof.ValidateKey(nexBusKey);
            m_Key = nexBusKey;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("X-Content-Type-Options", "nosniff");
            if (request == null || !string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
                return;
            }

            string node = request.Headers?["X-OGLVoice-Node"];
            string stamp = request.Headers?["X-OGLVoice-Timestamp"];
            string nonce = request.Headers?["X-OGLVoice-Nonce"];
            string signature = request.Headers?["X-OGLVoice-Signature"];
            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (!OglVoiceDiscoveryProof.VerifyRequest(m_Key, node, stamp, nonce, signature, now))
            {
                WriteError(response, HttpStatusCode.Unauthorized, "invalid_proof");
                return;
            }

            // Reject replayed signed challenges. Keep memory bounded; challenges
            // older than the permitted clock skew no longer need storage.
            lock (m_SeenLock)
            {
                long earliest = now.ToUnixTimeSeconds() - 121;
                List<string> expired = new();
                foreach (KeyValuePair<string, long> item in m_Seen)
                    if (item.Value < earliest)
                        expired.Add(item.Key);
                foreach (string expiredNonce in expired)
                    m_Seen.Remove(expiredNonce);

                string challenge = node + ":" + nonce;
                if (m_Seen.ContainsKey(challenge))
                {
                    WriteError(response, HttpStatusCode.Conflict, "replayed_challenge");
                    return;
                }
                if (m_Seen.Count >= MaxOutstandingChallenges)
                {
                    WriteError(response, HttpStatusCode.ServiceUnavailable, "challenge_capacity");
                    return;
                }
                m_Seen.Add(challenge, now.ToUnixTimeSeconds());
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(m_Provider);
            response.AddHeader("X-OGLVoice-Response-Signature",
                OglVoiceDiscoveryProof.ResponseSignature(m_Key, nonce, payload));
            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = payload;
        }

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string error)
        {
            response.StatusCode = (int)status;
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new { error });
        }
    }
}
