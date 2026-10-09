// SPDX-License-Identifier: MPL-2.0
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// HMAC-only simulator -> Robust IM event capture. Browser tokens cannot call
    /// this route. The transport is independent of the public citizen API.
    /// </summary>
    internal sealed class NexCitizenImIngress
    {
        private readonly byte[] m_Key;
        private readonly NexCitizenImStore m_Store;
        private readonly IUserAccountService m_Accounts;
        private readonly IFriendsService m_Friends;
        private const int MaxBody = 8192;

        internal NexCitizenImIngress(string sharedSecret, NexCitizenImStore store,
            IUserAccountService accounts, IFriendsService friends)
        {
            if (Encoding.UTF8.GetByteCount(sharedSecret ?? "") < 32)
                throw new ArgumentException("Portal IM transport requires at least 32 secret bytes.");
            m_Key = Encoding.UTF8.GetBytes(sharedSecret);
            m_Store = store;
            m_Accounts = accounts;
            m_Friends = friends;
        }

        internal void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                Respond(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
                return;
            }
            if ((request.UriPath ?? "").TrimEnd('/') != "/internal/nexportal/im/v1")
            {
                Respond(response, HttpStatusCode.NotFound, "not_found");
                return;
            }

            byte[] body;
            try
            {
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[2048];
                int count;
                while ((count = request.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + count > MaxBody)
                    {
                        Respond(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
                        return;
                    }
                    buffer.Write(chunk, 0, count);
                }
                body = buffer.ToArray();
            }
            catch
            {
                Respond(response, HttpStatusCode.BadRequest, "invalid_request");
                return;
            }

            string ts = request.Headers?["X-NexPortal-IM-Time"] ?? "";
            string signature = request.Headers?["X-NexPortal-IM-Signature"] ?? "";
            if (!long.TryParse(ts, out long stamped) ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamped) > 180 ||
                !TryValidateSignature(ts, body, signature))
            {
                Respond(response, HttpStatusCode.Unauthorized, "signature_invalid");
                return;
            }

            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                var obj = json.RootElement;
                if (obj.ValueKind != JsonValueKind.Object ||
                    !TryGuid(obj, "event_id", out Guid eventId) ||
                    !TryGuid(obj, "from_agent_id", out Guid sender) ||
                    !TryGuid(obj, "to_agent_id", out Guid receiver) ||
                    eventId == Guid.Empty || sender == Guid.Empty ||
                    receiver == Guid.Empty || sender == receiver ||
                    !obj.TryGetProperty("message", out JsonElement msg) ||
                    msg.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(msg.GetString()) ||
                    Encoding.UTF8.GetByteCount(msg.GetString()) > 1024)
                {
                    Respond(response, HttpStatusCode.BadRequest, "invalid_im");
                    return;
                }
                if (!obj.TryGetProperty("dialog", out JsonElement dialog) ||
                    !dialog.TryGetInt32(out int dialogCode) ||
                    dialogCode != (int)InstantMessageDialog.MessageFromAgent ||
                    !obj.TryGetProperty("from_group", out JsonElement group) ||
                    group.ValueKind != JsonValueKind.False)
                {
                    Respond(response, HttpStatusCode.BadRequest, "unsupported_im_type");
                    return;
                }

                UserAccount from = m_Accounts.GetUserAccount(UUID.Zero, new UUID(sender));
                UserAccount to = m_Accounts.GetUserAccount(UUID.Zero, new UUID(receiver));
                if (from == null || to == null || !from.Active || !to.Active ||
                    !from.LocalToGrid || !to.LocalToGrid)
                {
                    Respond(response, HttpStatusCode.Forbidden, "nonlocal_or_inactive");
                    return;
                }

                FriendInfo accepted = Array.Find(m_Friends.GetFriends(new UUID(sender))
                        ?? Array.Empty<FriendInfo>(),
                    f => f.Friend == receiver.ToString("D"));
                if (accepted == null || accepted.MyFlags < 0 || accepted.TheirFlags < 0)
                {
                    Respond(response, HttpStatusCode.Forbidden, "confirmed_friendship_required");
                    return;
                }

                string name = string.IsNullOrWhiteSpace(from.EffectiveDisplayName)
                    ? from.Name : from.EffectiveDisplayName;
                m_Store.Append(eventId, sender, receiver, name, msg.GetString(), "viewer");
                // Empty/disabled inbox is still an authorized no-op.
                Respond(response, HttpStatusCode.Accepted, "accepted");
            }
            catch (JsonException)
            {
                Respond(response, HttpStatusCode.BadRequest, "invalid_json");
            }
            catch
            {
                Respond(response, HttpStatusCode.ServiceUnavailable, "ingest_unavailable");
            }
        }

        private bool TryValidateSignature(string timestamp, byte[] payload, string given)
        {
            try
            {
                byte[] actual = Convert.FromBase64String(given);
                byte[] head = Encoding.ASCII.GetBytes(timestamp + "\n");
                byte[] input = new byte[head.Length + payload.Length];
                Buffer.BlockCopy(head, 0, input, 0, head.Length);
                Buffer.BlockCopy(payload, 0, input, head.Length, payload.Length);
                using var hmac = new HMACSHA256(m_Key);
                byte[] expected = hmac.ComputeHash(input);
                return actual.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryGuid(JsonElement obj, string key, out Guid id)
        {
            id = Guid.Empty;
            return obj.TryGetProperty(key, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(value.GetString(), out id);
        }

        private static void Respond(IOSHttpResponse response, HttpStatusCode status, string code)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new { status = code });
        }
    }
}
