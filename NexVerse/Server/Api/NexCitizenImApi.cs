// SPDX-License-Identifier: MPL-2.0

using System;
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
    /// Citizen-initiated in-world IM delivery. Only established local friendships
    /// may use this route; no impersonation, chat-history mining or grid-wide spam.
    /// Actual live/offline routing is delegated to OpenSim's IM service.
    /// </summary>
    internal sealed class NexCitizenImApi
    {
        private readonly IUserAccountService m_Accounts;
        private readonly IFriendsService m_Friends;
        private readonly NexApiAuthenticator m_Auth;
        private readonly IInstantMessage m_IM;
        private readonly INexAuditSink m_Audit;

        public NexCitizenImApi(
            IUserAccountService accounts,
            IFriendsService friends,
            NexApiAuthenticator auth,
            IInstantMessage instantMessages,
            INexAuditSink audit)
        {
            m_Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            m_Friends = friends ?? throw new ArgumentNullException(nameof(friends));
            m_Auth = auth ?? throw new ArgumentNullException(nameof(auth));
            m_IM = instantMessages;
            m_Audit = audit ?? NullNexAuditSink.Instance;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!string.Equals(request?.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                response.AddHeader("Allow", "POST");
                Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
                return;
            }
            if (!(request.UriPath ?? string.Empty).TrimEnd('/').Equals(
                "/api/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                Write(response, HttpStatusCode.NotFound, new { error = "not_found" });
                return;
            }
            if (!m_Auth.TryAuthenticate(request, NexScopes.RelationshipsWrite,
                    out NexPrincipal principal, out UserAccount sender,
                    out int statusCode, out string authError))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                Write(response, (HttpStatusCode)statusCode, new { error = authError });
                return;
            }

            // Never use an actor UUID or sender name provided by the browser.
            if (sender == null || !sender.Active || !sender.LocalToGrid ||
                !UUID.TryParse(principal.Subject, out UUID actor) ||
                actor != sender.PrincipalID)
            {
                Write(response, HttpStatusCode.Forbidden, new { error = "resident_sender_required" });
                return;
            }

            if (m_IM == null)
            {
                Write(response, HttpStatusCode.ServiceUnavailable,
                    new { error = "instant_message_service_unavailable" });
                return;
            }

            UUID target;
            string text;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                JsonElement body = doc.RootElement;
                if (body.ValueKind != JsonValueKind.Object ||
                    !body.TryGetProperty("to_agent_id", out JsonElement recipient) ||
                    recipient.ValueKind != JsonValueKind.String ||
                    !UUID.TryParse(recipient.GetString(), out target) || target.IsZero() ||
                    !body.TryGetProperty("message", out JsonElement content) ||
                    content.ValueKind != JsonValueKind.String)
                {
                    Write(response, HttpStatusCode.BadRequest, new { error = "invalid_message_request" });
                    return;
                }
                text = (content.GetString() ?? string.Empty).Trim();
            }
            catch (JsonException)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_json" });
                return;
            }

            if (target == sender.PrincipalID ||
                string.IsNullOrWhiteSpace(text) ||
                Encoding.UTF8.GetByteCount(text) > 1024)
            {
                Write(response, HttpStatusCode.BadRequest,
                    new { error = "invalid_message", message = "Message must contain between 1 and 1024 UTF-8 bytes." });
                return;
            }

            UserAccount receiver = m_Accounts.GetUserAccount(UUID.Zero, target);
            if (receiver == null || !receiver.Active || !receiver.LocalToGrid)
            {
                Write(response, HttpStatusCode.NotFound, new { error = "recipient_not_found" });
                return;
            }

            FriendInfo relationship = (m_Friends.GetFriends(sender.PrincipalID) ??
                Array.Empty<FriendInfo>()).FirstOrDefault(x => x.Friend == target.ToString());
            if (relationship == null || relationship.MyFlags < 0 || relationship.TheirFlags < 0)
            {
                Write(response, HttpStatusCode.Forbidden, new { error = "confirmed_friendship_required" });
                return;
            }

            string senderName = string.IsNullOrWhiteSpace(sender.EffectiveDisplayName)
                ? sender.Name : sender.EffectiveDisplayName;
            GridInstantMessage im = new GridInstantMessage(
                null, sender.PrincipalID, senderName, target,
                (byte)InstantMessageDialog.MessageFromAgent, text, true, Vector3.Zero);

            bool accepted;
            try
            {
                accepted = m_IM.IncomingInstantMessage(im);
            }
            catch (Exception)
            {
                // The handler intentionally does not log private message content.
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "message_delivery_unavailable" });
                return;
            }

            if (!accepted)
            {
                Write(response, HttpStatusCode.ServiceUnavailable, new { error = "message_not_delivered" });
                return;
            }

            m_Audit.Record(new NexAuditEvent(
                principal.Subject, "portal.im.sent", "message:" + target,
                details: new Dictionary<string, string>
                {
                    ["to_agent_id"] = target.ToString(),
                    ["bytes"] = Encoding.UTF8.GetByteCount(text).ToString()
                }));
            Write(response, HttpStatusCode.Accepted, new
            {
                status = "accepted",
                to_agent_id = target.ToString(),
                correlation_id = NexApiRequestContext.Ensure(response)
            });
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
