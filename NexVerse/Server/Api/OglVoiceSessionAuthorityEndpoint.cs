// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Voice;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Internal simulator-to-Robust LiveKit token exchange. The endpoint does
    /// not register any viewer capabilities or give clients a way to select
    /// tenants/rooms. Region-side presence and Estate/Parcel policy must first
    /// be asserted by an authorized simulator node.
    /// </summary>
    internal sealed class OglVoiceSessionAuthorityEndpoint
    {
        public const string Route = "/internal/oglvoice/v1/sessions";
        private readonly OglVoiceLiveKitTokenIssuer m_Issuer;
        private readonly OglVoiceProviderDescriptor m_Provider;
        private readonly string m_SharedKey;
        private readonly HashSet<string> m_AllowedNodes;
        private readonly Dictionary<string, long> m_Seen = new(StringComparer.Ordinal);
        private readonly object m_Lock = new();
        private const int MaxChallenges = 4096;

        public OglVoiceSessionAuthorityEndpoint(OglVoiceLiveKitTokenIssuer issuer,
            OglVoiceProviderDescriptor provider, string sharedKey, IEnumerable<string> allowedNodes)
        {
            m_Issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            m_Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            m_Provider.Validate();
            OglVoiceDiscoveryProof.ValidateKey(sharedKey);
            m_SharedKey = sharedKey;
            m_AllowedNodes = new HashSet<string>(allowedNodes ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            if (m_AllowedNodes.Count == 0 ||
                m_AllowedNodes.Contains("*"))
                throw new InvalidOperationException("OGLVoice session authority requires explicit AllowedNodes");
            foreach (string node in m_AllowedNodes)
                OglVoiceSessionProof.Sign(sharedKey, node,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OglVoiceDiscoveryProof.NewNonce(), new byte[] { 1 });
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("Pragma", "no-cache");
            response.AddHeader("X-Content-Type-Options", "nosniff");

            if (request == null || !string.Equals(request.HttpMethod, "POST",
                StringComparison.OrdinalIgnoreCase))
            {
                Error(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
                return;
            }

            string node = request.Headers?["X-OGLVoice-Node"];
            if (string.IsNullOrEmpty(node) || !m_AllowedNodes.Contains(node))
            {
                Error(response, HttpStatusCode.Forbidden, "node_not_allowed");
                return;
            }

            byte[] body;
            using (MemoryStream buffer = new())
            {
                byte[] chunk = new byte[4096];
                int count;
                // Cap before allocating arbitrary length input.
                while ((count = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + count > 4096)
                    {
                        Error(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
                        return;
                    }
                    buffer.Write(chunk, 0, count);
                }
                body = buffer.ToArray();
            }

            string timestamp = request.Headers?["X-OGLVoice-Timestamp"];
            string nonce = request.Headers?["X-OGLVoice-Nonce"];
            string signature = request.Headers?["X-OGLVoice-Signature"];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!OglVoiceSessionProof.Verify(m_SharedKey, node, timestamp,
                nonce, body, signature, now))
            {
                Error(response, HttpStatusCode.Unauthorized, "invalid_proof");
                return;
            }

            lock (m_Lock)
            {
                long expiry = now.ToUnixTimeSeconds() - 121;
                List<string> removals = new();
                foreach (var item in m_Seen)
                    if (item.Value < expiry)
                        removals.Add(item.Key);
                foreach (string old in removals) m_Seen.Remove(old);
                string key = node + ":" + nonce;
                if (m_Seen.ContainsKey(key))
                {
                    Error(response, HttpStatusCode.Conflict, "replayed_challenge");
                    return;
                }
                if (m_Seen.Count >= MaxChallenges)
                {
                    Error(response, HttpStatusCode.ServiceUnavailable, "challenge_capacity");
                    return;
                }
                m_Seen.Add(key, now.ToUnixTimeSeconds());
            }

            try
            {
                OglVoiceAdmission admission = JsonSerializer.Deserialize<OglVoiceAdmission>(body);
                if (admission == null ||
                    (admission.IsHypergridGuest && !m_Provider.hypergrid_guests) ||
                    admission.TenantId != m_Provider.tenant_id)
                {
                    Error(response, HttpStatusCode.Forbidden, "voice_not_authorized");
                    return;
                }

                OglVoiceIssuedToken token = m_Issuer.Issue(admission, now);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    access_token = token.Token,
                    room = token.Room,
                    identity = token.ParticipantIdentity,
                    expires_at = token.ExpiresAtUnix,
                    service_url = m_Provider.provider_url
                });
            }
            catch (Exception e) when (e is JsonException || e is ArgumentException ||
                                       e is UnauthorizedAccessException)
            {
                Error(response, HttpStatusCode.Forbidden, "voice_not_authorized");
            }
        }

        private static void Error(IOSHttpResponse response, HttpStatusCode status, string code)
        {
            response.StatusCode = (int)status;
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new { error = code });
        }
    }
}
