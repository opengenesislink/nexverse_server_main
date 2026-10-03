// SPDX-License-Identifier: MPL-2.0

using System;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexSecurityApi
    {
        private readonly NexApiAuthenticator m_Auth;
        private readonly INexSecurityStore m_Store;
        private readonly NexWebAuthnVerifier m_WebAuthn;

        public NexSecurityApi(NexApiAuthenticator auth, INexSecurityStore store, NexWebAuthnVerifier webAuthn)
        {
            m_Auth = auth;
            m_Store = store;
            m_WebAuthn = webAuthn;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!m_Auth.TryAuthenticate(request, NexScopes.SecurityManage, out NexPrincipal principal, out UserAccount account, out int status, out string error) || account == null)
            {
                Write(response, (HttpStatusCode)status, new { error });
                return;
            }

            string subject = account.PrincipalID.ToString();
            string[] p = (request.UriPath ?? string.Empty).Trim('/').Split('/');

            if (p.Length == 5 && p[4] == "sessions" && request.HttpMethod == "GET")
            {
                Write(response, HttpStatusCode.OK, new { sessions = m_Store.ListSessions(subject) });
                return;
            }

            if (p.Length == 6 && p[4] == "sessions" && request.HttpMethod == "DELETE")
            {
                bool ok = m_Store.RevokeSession(subject, p[5]);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.NotFound, new { status = ok ? "revoked" : "not_found" });
                return;
            }

            if (p.Length == 5 && p[4] == "events" && request.HttpMethod == "GET")
            {
                Write(response, HttpStatusCode.OK, new { events = m_Store.History(subject, 100) });
                return;
            }

            if (p.Length == 6 && p[4] == "totp" && p[5] == "enroll" && request.HttpMethod == "POST")
            {
                string secret = m_Store.EnsureTotpSecret(subject);
                m_Store.Record(subject, "totp.enrolled", true);
                Write(response, HttpStatusCode.Created, new { secret, algorithm = "SHA1", digits = 6, period = 30 });
                return;
            }

            if (p.Length == 6 && p[4] == "totp" && p[5] == "verify" && request.HttpMethod == "POST")
            {
                string code = ReadCode(request);
                bool ok = m_Store.VerifyTotp(subject, code);
                m_Store.Record(subject, "totp.verify", ok);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, new { verified = ok });
                return;
            }

            if (p.Length == 6 && p[4] == "webauthn" && p[5] == "challenge" && request.HttpMethod == "POST")
            {
                NexWebAuthnChallenge challenge = m_Store.CreateWebAuthnChallenge(subject, 300);
                Write(response, HttpStatusCode.Created, new { challenge_id = challenge.Id, challenge = challenge.Challenge, expires_at = challenge.ExpiresAt });
                return;
            }

            if (p.Length == 6 && p[4] == "webauthn" && p[5] == "verify" && request.HttpMethod == "POST")
            {
                VerifyWebAuthn(request, response, subject);
                return;
            }

            if (p.Length == 5 && p[4] == "passkeys" && request.HttpMethod == "GET")
            {
                Write(response, HttpStatusCode.OK, new { passkeys = m_Store.ListPasskeys(subject) });
                return;
            }

            if (p.Length == 6 && p[4] == "passkeys" && p[5] == "register" && request.HttpMethod == "POST")
            {
                NexPasskeyCredential credential = ReadPasskey(request, subject);
                bool ok = m_Store.RegisterPasskey(credential);
                m_Store.Record(subject, "passkey.register", ok);
                Write(response, ok ? HttpStatusCode.Created : HttpStatusCode.BadRequest,
                    new { registered = ok, credential_id = credential?.CredentialId ?? string.Empty });
                return;
            }

            if (p.Length == 6 && p[4] == "passkeys" && request.HttpMethod == "DELETE")
            {
                bool ok = m_Store.RemovePasskey(subject, p[5]);
                m_Store.Record(subject, "passkey.remove", ok);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.NotFound, new { removed = ok });
                return;
            }

            if (p.Length == 5 && p[4] == "totp" && request.HttpMethod == "DELETE")
            {
                bool ok = m_Store.DisableTotp(subject);
                m_Store.Record(subject, "totp.disabled", ok);
                Write(response, HttpStatusCode.OK, new { disabled = ok });
                return;
            }

            Write(response, HttpStatusCode.NotFound, new { error = "security_route_not_found" });
        }



        private void VerifyWebAuthn(IOSHttpRequest request, IOSHttpResponse response, string subject)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                JsonElement root = doc.RootElement;
                string challengeId = root.GetProperty("challenge_id").GetString() ?? string.Empty;
                string challenge = root.GetProperty("challenge").GetString() ?? string.Empty;
                string credentialId = root.GetProperty("credential_id").GetString() ?? string.Empty;
                NexPasskeyCredential credential = m_Store.GetPasskey(subject, credentialId);
                bool challengeOk = credential != null && m_Store.TryConsumeWebAuthnChallenge(subject, challengeId, challenge);
                bool assertionOk = challengeOk && m_WebAuthn != null && m_WebAuthn.VerifyAssertion(
                    credential, challenge,
                    root.GetProperty("client_data_json").GetString(),
                    root.GetProperty("authenticator_data").GetString(),
                    root.GetProperty("signature").GetString(),
                    out long signCount);
                bool counterOk = assertionOk && m_Store.UpdatePasskeyCounter(subject, credentialId, signCount);
                bool ok = assertionOk && counterOk;
                m_Store.Record(subject, "passkey.verify", ok);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, new { verified = ok });
            }
            catch
            {
                m_Store.Record(subject, "passkey.verify", false);
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_webauthn_assertion" });
            }
        }

        private static NexPasskeyCredential ReadPasskey(IOSHttpRequest request, string subject)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                JsonElement root = doc.RootElement;
                string id = root.TryGetProperty("credential_id", out JsonElement credentialId) ? credentialId.GetString() ?? string.Empty : string.Empty;
                string key = root.TryGetProperty("public_key", out JsonElement publicKey) ? publicKey.GetString() ?? string.Empty : string.Empty;
                string name = root.TryGetProperty("name", out JsonElement displayName) ? displayName.GetString() ?? "Passkey" : "Passkey";
                if (id.Length > 1024 || key.Length > 8192 || name.Length > 128) return null;
                return new NexPasskeyCredential { Subject = subject, CredentialId = id.Trim(), PublicKey = key.Trim(), Name = name.Trim() };
            }
            catch { return null; }
        }

        private static string ReadCode(IOSHttpRequest request)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                return doc.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() ?? string.Empty : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static void Write(IOSHttpResponse response, HttpStatusCode status, object payload)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.RawBuffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        }
    }
}
