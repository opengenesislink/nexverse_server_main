// SPDX-License-Identifier: MPL-2.0

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using NexVerse.Core.Audit;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace NexVerse.Server.Api
{
    internal sealed class NexSocialGraphApi
    {
        private readonly IUserAccountService m_Accounts;
        private readonly IFriendsService m_Friends;
        private readonly NexApiAuthenticator m_Auth;
        private readonly IMuteListService m_Mutes;
        private readonly INexAuditSink m_Audit;

        public NexSocialGraphApi(IUserAccountService accounts, IFriendsService friends, NexApiAuthenticator auth, IMuteListService mutes, INexAuditSink audit)
        {
            m_Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            m_Friends = friends ?? throw new ArgumentNullException(nameof(friends));
            m_Auth = auth ?? throw new ArgumentNullException(nameof(auth));
            m_Mutes = mutes ?? throw new ArgumentNullException(nameof(mutes));
            m_Audit = audit ?? NullNexAuditSink.Instance;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!Authenticate(request, response, out NexPrincipal principal))
                return;

            string[] p = (request?.UriPath ?? string.Empty).Trim('/').Split('/');
            if (p.Length < 4 || !UUID.TryParse(p[3], out UUID owner))
            {
                Write(response, HttpStatusCode.NotFound, new { error = "not_found" });
                return;
            }

            if (!Owns(principal, owner))
            {
                Write(response, HttpStatusCode.Forbidden, new { error = "relationship_owner_required" });
                return;
            }

            if (p.Length == 4 && request.HttpMethod == "GET")
            {
                FriendInfo[] friends = m_Friends.GetFriends(owner) ?? Array.Empty<FriendInfo>();
                Write(response, HttpStatusCode.OK, new
                {
                    principal_id = owner.ToString(),
                    relationships = friends.Select(Project).ToArray()
                });
                return;
            }

            if (p.Length != 5 || !UUID.TryParse(p[4], out UUID target) || target == owner)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_relationship_target" });
                return;
            }

            if (m_Accounts.GetUserAccount(UUID.Zero, target) == null)
            {
                Write(response, HttpStatusCode.NotFound, new { error = "target_not_found" });
                return;
            }

            if (request.HttpMethod == "POST")
            {
                // OpenSim represents a pending offer by storing -1 on the target-facing row.
                bool a = m_Friends.StoreFriend(owner.ToString(), target.ToString(), 1);
                bool b = m_Friends.StoreFriend(target.ToString(), owner.ToString(), -1);
                Write(response, a && b ? HttpStatusCode.Accepted : HttpStatusCode.InternalServerError,
                    new { status = a && b ? "pending" : "failed", principal_id = owner.ToString(), target_id = target.ToString() });
                return;
            }

            if (request.HttpMethod == "PUT")
            {
                int rights = ReadRights(request, 1);
                bool a = m_Friends.StoreFriend(owner.ToString(), target.ToString(), rights);
                bool b = m_Friends.StoreFriend(target.ToString(), owner.ToString(), rights);
                Write(response, a && b ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                    new { status = a && b ? "accepted" : "failed", rights });
                return;
            }

            if (request.HttpMethod == "PATCH")
            {
                int rights = ReadRights(request, 1);
                bool ok = m_Friends.StoreFriend(owner.ToString(), target.ToString(), rights);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                    new { status = ok ? "updated" : "failed", rights });
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                bool a = m_Friends.Delete(owner, target.ToString());
                bool b = m_Friends.Delete(target, owner.ToString());
                Write(response, HttpStatusCode.OK, new { status = (a || b) ? "removed" : "absent" });
                return;
            }

            response.AddHeader("Allow", "GET, POST, PUT, PATCH, DELETE");
            Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
        }

        private object Project(FriendInfo f)
        {
            UUID.TryParse(f.Friend, out UUID id);
            UserAccount account = id.IsZero() ? null : m_Accounts.GetUserAccount(UUID.Zero, id);
            return new
            {
                principal_id = id.ToString(),
                username = account?.Username ?? string.Empty,
                display_name = account?.EffectiveDisplayName ?? string.Empty,
                state = f.TheirFlags == -1 ? "incoming_pending" : (f.MyFlags == -1 ? "outgoing_pending" : "friends"),
                rights = f.MyFlags,
                their_rights = f.TheirFlags,
                can_see_online = (f.MyFlags & 1) != 0,
                can_see_on_map = (f.MyFlags & 2) != 0,
                can_modify_objects = (f.MyFlags & 4) != 0
            };
        }

        private bool Authenticate(IOSHttpRequest request, IOSHttpResponse response, out NexPrincipal principal)
        {
            if (m_Auth.TryAuthenticate(request, NexScopes.RelationshipsWrite, out principal, out UserAccount _, out int status, out string error))
                return true;
            response.AddHeader("WWW-Authenticate", "Bearer");
            Write(response, (HttpStatusCode)status, new { error });
            return false;
        }

        private static bool Owns(NexPrincipal p, UUID owner) =>
            (UUID.TryParse(p.Subject, out UUID subject) && subject == owner) || p.HasScope(NexScopes.AdminAll);

        private static int ReadRights(IOSHttpRequest request, int fallback)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                return doc.RootElement.TryGetProperty("rights", out JsonElement e) && e.TryGetInt32(out int value)
                    ? value & 7
                    : fallback;
            }
            catch
            {
                return fallback;
            }
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
