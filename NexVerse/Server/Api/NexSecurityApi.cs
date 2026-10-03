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

        public NexSecurityApi(NexApiAuthenticator auth, INexSecurityStore store)
        {
            m_Auth = auth;
            m_Store = store;
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

            if (p.Length == 5 && p[4] == "totp" && request.HttpMethod == "DELETE")
            {
                bool ok = m_Store.DisableTotp(subject);
                m_Store.Record(subject, "totp.disabled", ok);
                Write(response, HttpStatusCode.OK, new { disabled = ok });
                return;
            }

            Write(response, HttpStatusCode.NotFound, new { error = "security_route_not_found" });
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
