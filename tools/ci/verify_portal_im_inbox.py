#!/usr/bin/env python3
"""Security/route/source regression for the opt-in bidirectional citizen IM."""
from pathlib import Path

api=Path("NexVerse/Server/Api/NexCitizenImApi.cs").read_text()
ingress=Path("NexVerse/Server/Api/NexCitizenImIngress.cs").read_text()
store=Path("NexVerse/Server/Api/NexCitizenImStore.cs").read_text()
forwarder=Path("OpenSim/Region/CoreModules/Avatar/InstantMessage/NexCitizenImForwarder.cs").read_text()
module=Path("OpenSim/Region/CoreModules/Avatar/InstantMessage/HGMessageTransferModule.cs").read_text()
connector=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
openapi=Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text()
prebuild=Path("prebuild.xml").read_text()
docs=Path("doc/NexVerse/PORTAL_IM_CHAT.md").read_text()

for item in (
    "m_Auth.TryAuthenticate(request,",
    "owner != actorAccount.PrincipalID",
    "read ? NexScopes.RelationshipsRead : NexScopes.RelationshipsWrite",
    '"/api/v1/messages/events"',
    '"/api/v1/messages/settings"',
    '"/api/v1/messages/history"',
    '"/api/v1/messages/with/"',
    "m_Store.Read(owner.Guid, after, limit, peer)",
    "m_Store.SetEnabled(owner.Guid, enabled)",
    "m_Store.DeleteHistory(owner.Guid)",
    "TakeSendQuota(sender.PrincipalID.Guid)",
    "confirmed_friendship_required",
    "Encoding.UTF8.GetByteCount(body) > 1024",
    "m_Store.Append(eventId, sender.PrincipalID.Guid",
    "m_IM.IncomingInstantMessage(im)",
):
    assert item in api,item
assert api.index("m_IM.IncomingInstantMessage(im)") < api.index("m_Store.Append(eventId"), "do not persist before send accepted"

for item in (
    "portal_im_settings",
    "portal_im_entries",
    "UNIQUE(owner, event_id)",
    "WHERE owner=@owner",
    "WHERE EXISTS (SELECT 1 FROM portal_im_settings",
    "DELETE FROM portal_im_entries WHERE owner=@owner",
    "Math.Clamp(retentionDays, 7, 365)",
    "MaxEntriesPerOwner = 10000",
    "Math.Clamp(limit,1,100)",
    "ON CONFLICT(owner)",
):
    assert item in store,item
for item in (
    '"/internal/nexportal/im/v1"',
    '"X-NexPortal-IM-Time"',
    '"X-NexPortal-IM-Signature"',
    "CryptographicOperations.FixedTimeEquals",
    "Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamped) > 180",
    "InstantMessageDialog.MessageFromAgent",
    "!from.LocalToGrid || !to.LocalToGrid",
    "confirmed_friendship_required",
    "m_Store.Append(eventId, sender, receiver",
):
    assert item in ingress,item
for item in (
    "Encoding.UTF8.GetBytes(timestamp +",
    "HMACSHA256",
    "SemaphoreSlim(48, 48)",
    "if (!m_Queue.Wait(0))",
    "if (result.IsSuccessStatusCode)",
    'url.AbsolutePath != "/internal/nexportal/im/v1"',
    "im.fromGroup",
    "im.dialog != (byte)InstantMessageDialog.MessageFromAgent",
):
    assert item in forwarder,item
assert "m_PortalImForwarder = NexCitizenImForwarder.FromConfig(config);" in module
assert "recordResult(true);" in module
assert "HandleUndeliverableMessage(im, recordResult);" in module
assert "m_PortalImForwarder?.Accepted(im);" in module
assert "new NexCitizenImStore(" in connector
assert "new NexCitizenImIngress(" in connector
assert '"/internal/nexportal/im/v1"' in connector
assert "NEX_PORTAL_IM_SECRET" in connector
assert '<Reference name="Mono.Data.Sqlite"/>' in prebuild
for item in (
    '["/api/v1/messages/events"]',
    '["/api/v1/messages/settings"]',
    '["/api/v1/messages/history"]',
    "CitizenImEventsResponse",
    "CitizenImConversationsResponse",
):
    assert item in openapi,item
for item in ("Opt-in","PATCH /api/v1/messages/settings","GET /api/v1/messages/events",
             "DELETE /messages/history","Firestorm","HMAC","5 Sekunden"):
    assert item.lower() in docs.lower(),item
print("Portal IM bidirectional opt-in source/security contract: OK")
