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
        private readonly IPresenceService m_Presence;

        public NexSocialGraphApi(IUserAccountService accounts, IFriendsService friends, NexApiAuthenticator auth, IMuteListService mutes, INexAuditSink audit, IPresenceService presence)
        {
            m_Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            m_Friends = friends ?? throw new ArgumentNullException(nameof(friends));
            m_Auth = auth ?? throw new ArgumentNullException(nameof(auth));
            m_Mutes = mutes ?? throw new ArgumentNullException(nameof(mutes));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_Presence = presence;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!Authenticate(request, response, string.Equals(request?.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ? NexScopes.RelationshipsRead : NexScopes.RelationshipsWrite, out NexPrincipal principal))
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

            if (p.Length == 5 && p[4].Equals("blocks", StringComparison.OrdinalIgnoreCase))
            {
                HandleBlocks(request, response, principal, owner);
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

            FriendInfo current = (m_Friends.GetFriends(owner) ?? Array.Empty<FriendInfo>())
                .FirstOrDefault(x => x.Friend == target.ToString());

            if (request.HttpMethod == "POST")
            {
                if (current != null)
                {
                    Write(response, HttpStatusCode.Conflict, new { error = "relationship_already_exists" });
                    return;
                }

                // The incoming viewer offers list selects TheirFlags == -1.
                // Therefore -1 must be the SENDER-facing row. Reversing these
                // flags would display the request on the wrong avatar.
                bool a = m_Friends.StoreFriend(owner.ToString(), target.ToString(), -1);
                bool b = a && m_Friends.StoreFriend(target.ToString(), owner.ToString(), 1);
                if (!a || !b)
                {
                    m_Friends.Delete(owner, target.ToString());
                    m_Friends.Delete(target, owner.ToString());
                }
                Audit(principal, "relationship.request", owner, target, a && b);
                Write(response, a && b ? HttpStatusCode.Accepted : HttpStatusCode.InternalServerError,
                    new { status = a && b ? "pending" : "failed", principal_id = owner.ToString(), target_id = target.ToString() });
                return;
            }

            if (request.HttpMethod == "PUT")
            {
                // Only the recipient of an outstanding offer may accept it;
                // a sender cannot accept their own request.
                if (current == null || current.TheirFlags != -1 || current.MyFlags == -1)
                {
                    Write(response, HttpStatusCode.Conflict, new { error = "no_incoming_friend_request" });
                    return;
                }
                int rights = ReadRights(request, 1);
                bool a = m_Friends.StoreFriend(owner.ToString(), target.ToString(), rights);
                bool b = a && m_Friends.StoreFriend(target.ToString(), owner.ToString(), 1);
                Audit(principal, "relationship.accept", owner, target, a && b);
                Write(response, a && b ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                    new { status = a && b ? "accepted" : "failed", rights });
                return;
            }

            if (request.HttpMethod == "PATCH")
            {
                if (current == null || current.MyFlags < 0 || current.TheirFlags < 0)
                {
                    Write(response, HttpStatusCode.Conflict, new { error = "friendship_not_accepted" });
                    return;
                }
                int rights = ReadRights(request, current.MyFlags);
                bool ok = m_Friends.StoreFriend(owner.ToString(), target.ToString(), rights);
                Audit(principal, "relationship.rights", owner, target, ok);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                    new { status = ok ? "updated" : "failed", rights });
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                bool pending = current != null && (current.MyFlags == -1 || current.TheirFlags == -1);
                bool a = m_Friends.Delete(owner, target.ToString());
                bool b = m_Friends.Delete(target, owner.ToString());
                string action = pending ? "relationship.decline" : "relationship.remove";
                Audit(principal, action, owner, target, a || b);
                Write(response, HttpStatusCode.OK, new { status = (a || b) ? "removed" : "absent" });
                return;
            }

            response.AddHeader("Allow", "GET, POST, PUT, PATCH, DELETE");
            Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
        }


        private void HandleBlocks(IOSHttpRequest request, IOSHttpResponse response, NexPrincipal principal, UUID owner)
        {
            string[] p = (request?.UriPath ?? string.Empty).Trim('/').Split('/');
            if (p.Length != 6 || !UUID.TryParse(p[5], out UUID target) || target == owner)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_block_target" });
                return;
            }

            UserAccount targetAccount = m_Accounts.GetUserAccount(UUID.Zero, target);
            if (targetAccount == null)
            {
                Write(response, HttpStatusCode.NotFound, new { error = "target_not_found" });
                return;
            }

            if (request.HttpMethod == "PUT" || request.HttpMethod == "POST")
            {
                MuteData mute = new MuteData
                {
                    AgentID = owner,
                    MuteID = target,
                    MuteName = targetAccount.Name,
                    MuteType = 1,
                    MuteFlags = 0,
                    Stamp = Util.UnixTimeSinceEpoch()
                };
                bool ok = m_Mutes.UpdateMute(mute);
                if (ok)
                {
                    // A blocked resident must not remain eligible for
                    // friend-only portal messaging.
                    m_Friends.Delete(owner, target.ToString());
                    m_Friends.Delete(target, owner.ToString());
                }
                Audit(principal, "relationship.block", owner, target, ok);
                Write(response, ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError,
                    new { status = ok ? "blocked" : "failed", target_id = target.ToString() });
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                bool ok = m_Mutes.RemoveMute(owner, target, targetAccount.Name);
                Audit(principal, "relationship.unblock", owner, target, ok);
                Write(response, HttpStatusCode.OK,
                    new { status = ok ? "unblocked" : "absent", target_id = target.ToString() });
                return;
            }

            response.AddHeader("Allow", "PUT, POST, DELETE");
            Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
        }

        private void Audit(NexPrincipal principal, string action, UUID owner, UUID target, bool success)
        {
            m_Audit.Record(new NexAuditEvent(
                principal?.Subject ?? owner.ToString(),
                action,
                "relationship:" + owner,
                details: new Dictionary<string, string>
                {
                    ["owner_id"] = owner.ToString(),
                    ["target_id"] = target.ToString(),
                    ["success"] = success ? "true" : "false"
                }));
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
                // TheirFlags describe what the friend grants this avatar.
                can_see_online = f.MyFlags >= 0 && f.TheirFlags >= 0 && (f.TheirFlags & 1) != 0,
                online = f.MyFlags >= 0 && f.TheirFlags >= 0 && (f.TheirFlags & 1) != 0 && IsOnline(id),
                can_see_on_map = f.MyFlags >= 0 && f.TheirFlags >= 0 && (f.TheirFlags & 2) != 0,
                can_modify_objects = f.MyFlags >= 0 && f.TheirFlags >= 0 && (f.TheirFlags & 4) != 0
            };
        }


        private bool IsOnline(UUID id)
        {
            if (m_Presence == null || id.IsZero()) return false;
            PresenceInfo[] presences = m_Presence.GetAgents(new[] { id.ToString() });
            return presences != null && presences.Any(x => x != null && !x.RegionID.IsZero());
        }

        private bool Authenticate(IOSHttpRequest request, IOSHttpResponse response, string scope, out NexPrincipal principal)
        {
            if (m_Auth.TryAuthenticate(request, scope, out principal, out UserAccount _, out int status, out string error))
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
