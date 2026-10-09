// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Authenticated local-avatar conversation API. Viewer delivery and offline
    /// IM storage remain in OpenSim. Transcript recording is opt-in per avatar.
    /// </summary>
    internal sealed class NexCitizenImApi
    {
        private readonly IUserAccountService m_Accounts;
        private readonly IFriendsService m_Friends;
        private readonly NexApiAuthenticator m_Auth;
        private readonly IInstantMessage m_IM;
        private readonly INexAuditSink m_Audit;
        private readonly NexCitizenImStore m_Store;
        private readonly ConcurrentDictionary<Guid, Queue<long>> m_SendWindows =
            new ConcurrentDictionary<Guid, Queue<long>>();

        public NexCitizenImApi(IUserAccountService accounts, IFriendsService friends,
            NexApiAuthenticator auth, IInstantMessage instantMessages,
            INexAuditSink audit, NexCitizenImStore store = null)
        {
            m_Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            m_Friends = friends ?? throw new ArgumentNullException(nameof(friends));
            m_Auth = auth ?? throw new ArgumentNullException(nameof(auth));
            m_IM = instantMessages;
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_Store = store;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request.UriPath ?? "").TrimEnd('/');
            string method = request.HttpMethod?.ToUpperInvariant() ?? "";
            bool read = method == "GET";
            if (path != "/api/v1/messages" &&
                path != "/api/v1/messages/events" &&
                path != "/api/v1/messages/settings" &&
                path != "/api/v1/messages/history" &&
                !path.StartsWith("/api/v1/messages/with/", StringComparison.Ordinal))
            {
                Write(response, HttpStatusCode.NotFound, new { error = "not_found" });
                return;
            }

            if (!m_Auth.TryAuthenticate(request,
                    read ? NexScopes.RelationshipsRead : NexScopes.RelationshipsWrite,
                    out NexPrincipal principal, out UserAccount actorAccount,
                    out int status, out string authError))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                Write(response, (HttpStatusCode)status, new { error = authError });
                return;
            }

            // Do not allow an admin token or service key to masquerade as a user.
            if (actorAccount == null || !actorAccount.Active || !actorAccount.LocalToGrid ||
                !UUID.TryParse(principal.Subject, out UUID owner) ||
                owner.IsZero() || owner != actorAccount.PrincipalID)
            {
                Write(response, HttpStatusCode.Forbidden, new { error = "resident_sender_required" });
                return;
            }

            if (path == "/api/v1/messages" && method == "POST")
            {
                Send(request, response, principal, actorAccount);
                return;
            }

            if (m_Store == null)
            {
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "portal_im_archive_disabled" });
                return;
            }

            try
            {
                if (path == "/api/v1/messages/settings")
                {
                    if (method == "GET")
                    {
                        Write(response, HttpStatusCode.OK, new
                        {
                            enabled = m_Store.IsEnabled(owner.Guid),
                            archive = "opt_in",
                            retention_days = "configured",
                            polling_interval_seconds = 5
                        });
                        return;
                    }
                    if (method == "PATCH")
                    {
                        using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                            !doc.RootElement.TryGetProperty("enabled", out JsonElement flag) ||
                            (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
                        {
                            Write(response, HttpStatusCode.BadRequest, new { error = "invalid_settings" });
                            return;
                        }
                        bool enabled = flag.GetBoolean();
                        m_Store.SetEnabled(owner.Guid, enabled);
                        m_Audit.Record(new NexAuditEvent(principal.Subject,
                            enabled ? "portal.im.enable" : "portal.im.disable", "portal-im:" + owner));
                        Write(response, HttpStatusCode.OK, new { enabled, history_deleted = !enabled });
                        return;
                    }
                }

                if (path == "/api/v1/messages/history" && method == "DELETE")
                {
                    m_Store.DeleteHistory(owner.Guid);
                    m_Audit.Record(new NexAuditEvent(principal.Subject,
                        "portal.im.history_delete", "portal-im:" + owner));
                    Write(response, HttpStatusCode.OK, new { deleted = true });
                    return;
                }

                if (method == "GET" && path == "/api/v1/messages")
                {
                    List<object> conversations = m_Store.Conversations(owner.Guid,
                        QueryNumber(request, "limit", 50, 1, 100));
                    Write(response, HttpStatusCode.OK, new
                    {
                        conversations,
                        count = conversations.Count,
                        correlation_id = NexApiRequestContext.Ensure(response)
                    });
                    return;
                }

                if (method == "GET" &&
                    (path == "/api/v1/messages/events" ||
                     path.StartsWith("/api/v1/messages/with/", StringComparison.Ordinal)))
                {
                    Guid? peer = null;
                    if (path.StartsWith("/api/v1/messages/with/", StringComparison.Ordinal))
                    {
                        string uuid = path.Substring("/api/v1/messages/with/".Length);
                        if (!Guid.TryParse(uuid, out Guid target) || target == Guid.Empty || target == owner.Guid)
                        {
                            Write(response, HttpStatusCode.BadRequest, new { error = "invalid_peer" });
                            return;
                        }
                        peer = target;
                    }
                    long after = QueryCursor(request, "after");
                    int limit = QueryNumber(request, "limit", 50, 1, 100);
                    List<NexCitizenImEntry> entries = m_Store.Read(owner.Guid, after, limit, peer);
                    Write(response, HttpStatusCode.OK, new
                    {
                        messages = entries,
                        count = entries.Count,
                        next_cursor = entries.Count > 0 ? entries[^1].seq : after,
                        poll_after_seconds = 5,
                        correlation_id = NexApiRequestContext.Ensure(response)
                    });
                    return;
                }

                response.AddHeader("Allow", path == "/api/v1/messages/settings"
                    ? "GET, PATCH" : path == "/api/v1/messages/history"
                    ? "DELETE" : path == "/api/v1/messages"
                    ? "GET, POST" : "GET");
                Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
            }
            catch (JsonException)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_json" });
            }
            catch (ArgumentException)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_query" });
            }
            catch (Exception)
            {
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "portal_im_store_unavailable" });
            }
        }

        private void Send(IOSHttpRequest request, IOSHttpResponse response,
            NexPrincipal principal, UserAccount sender)
        {
            if (m_IM == null)
            {
                Write(response, HttpStatusCode.ServiceUnavailable,
                    new { error = "instant_message_service_unavailable" });
                return;
            }

            UUID target;
            string body;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                JsonElement json = doc.RootElement;
                if (json.ValueKind != JsonValueKind.Object ||
                    !json.TryGetProperty("to_agent_id", out JsonElement recipient) ||
                    recipient.ValueKind != JsonValueKind.String ||
                    !UUID.TryParse(recipient.GetString(), out target) || target.IsZero() ||
                    !json.TryGetProperty("message", out JsonElement text) ||
                    text.ValueKind != JsonValueKind.String)
                {
                    Write(response, HttpStatusCode.BadRequest, new { error = "invalid_message_request" });
                    return;
                }
                body = (text.GetString() ?? "").Trim();
            }
            catch (JsonException)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_json" });
                return;
            }

            if (target == sender.PrincipalID || string.IsNullOrWhiteSpace(body) ||
                Encoding.UTF8.GetByteCount(body) > 1024)
            {
                Write(response, HttpStatusCode.BadRequest, new
                { error = "invalid_message", message = "Message must be between 1 and 1024 UTF-8 bytes." });
                return;
            }

            UserAccount receiver = m_Accounts.GetUserAccount(UUID.Zero, target);
            if (receiver == null || !receiver.Active || !receiver.LocalToGrid)
            {
                Write(response, HttpStatusCode.NotFound, new { error = "recipient_not_found" });
                return;
            }
            FriendInfo friend = (m_Friends.GetFriends(sender.PrincipalID) ??
                Array.Empty<FriendInfo>()).FirstOrDefault(x => x.Friend == target.ToString());
            if (friend == null || friend.MyFlags < 0 || friend.TheirFlags < 0)
            {
                Write(response, HttpStatusCode.Forbidden, new { error = "confirmed_friendship_required" });
                return;
            }

            if (!TakeSendQuota(sender.PrincipalID.Guid))
            {
                response.AddHeader("Retry-After", "60");
                Write(response, HttpStatusCode.TooManyRequests, new { error = "im_send_rate_limited" });
                return;
            }

            string senderName = string.IsNullOrWhiteSpace(sender.EffectiveDisplayName)
                ? sender.Name : sender.EffectiveDisplayName;
            GridInstantMessage im = new GridInstantMessage(null, sender.PrincipalID,
                senderName, target, (byte)InstantMessageDialog.MessageFromAgent,
                body, false, Vector3.Zero);

            bool accepted;
            try { accepted = m_IM.IncomingInstantMessage(im); }
            catch
            {
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "message_delivery_unavailable" });
                return;
            }
            if (!accepted)
            {
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "message_not_delivered" });
                return;
            }

            Guid eventId = Guid.NewGuid();
            bool archived = false;
            try
            {
                if (m_Store != null)
                    archived = m_Store.Append(eventId, sender.PrincipalID.Guid, target.Guid,
                        senderName, body, "portal");
            }
            catch
            {
                // Delivery already succeeded; never retransmit merely because
                // the optional archive failed (that would duplicate IMs).
            }

            m_Audit.Record(new NexAuditEvent(principal.Subject, "portal.im.sent",
                "message:" + target, details: new Dictionary<string, string>
                {
                    ["to_agent_id"] = target.ToString(),
                    ["bytes"] = Encoding.UTF8.GetByteCount(body).ToString()
                }));
            Write(response, HttpStatusCode.Accepted, new
            {
                status = "accepted",
                event_id = eventId,
                to_agent_id = target.ToString(),
                archived,
                correlation_id = NexApiRequestContext.Ensure(response)
            });
        }

        private bool TakeSendQuota(Guid owner)
        {
            var queue = m_SendWindows.GetOrAdd(owner, _ => new Queue<long>());
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (queue)
            {
                while (queue.Count != 0 && queue.Peek() <= now - 60)
                    queue.Dequeue();
                if (queue.Count >= 30) return false;
                queue.Enqueue(now);
                return true;
            }
        }

        private static int QueryNumber(IOSHttpRequest request, string name, int fallback, int min, int max)
        {
            string raw = request.QueryString?[name];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, out int count) || count < min || count > max)
                throw new ArgumentException(name);
            return count;
        }

        private static long QueryCursor(IOSHttpRequest request, string name)
        {
            string raw = request.QueryString?[name];
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (!long.TryParse(raw, out long value) || value < 0)
                throw new ArgumentException(name);
            return value;
        }

        private static void Write(IOSHttpResponse response, HttpStatusCode code, object payload)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)code;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload);
        }
    }
}
