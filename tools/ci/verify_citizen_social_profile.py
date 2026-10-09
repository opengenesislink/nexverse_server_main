#!/usr/bin/env python3
"""Contract checks for resident portal scopes, friends, profiles and in-world IM.

These checks supplement the release compilation and runtime smoke suite.
"""
from pathlib import Path
security=Path("NexVerse/Core/Security/NexSecurity.cs").read_text(encoding="utf-8")
social=Path("NexVerse/Server/Api/NexSocialGraphApi.cs").read_text(encoding="utf-8")
profile=Path("NexVerse/Server/Api/NexWebProfileV3Api.cs").read_text(encoding="utf-8")
im=Path("NexVerse/Server/Api/NexCitizenImApi.cs").read_text(encoding="utf-8")
connector=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
schema=Path("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8")
docs=Path("doc/NexVerse/PORTAL_SOCIAL_PROFILE.md").read_text(encoding="utf-8")

section=security.split("public static string[] GetEffectiveScopes(",1)[1].split("foreach (string role",1)[0]
for scope in ("NexScopes.ProfileRead", "NexScopes.ProfileWrite", "NexScopes.RelationshipsRead",
              "NexScopes.RelationshipsWrite", "NexScopes.GroupsRead", "NexScopes.GroupsManage"):
    assert scope in section, "missing default citizen scope "+scope

assert 'm_Friends.StoreFriend(owner.ToString(), target.ToString(), -1)' in social
assert 'm_Friends.StoreFriend(target.ToString(), owner.ToString(), 1)' in social
assert 'current.TheirFlags != -1' in social
assert 'current.MyFlags < 0 || current.TheirFlags < 0' in social
assert 'FriendInfo current' in social
assert 'string action = pending ? "relationship.decline" : "relationship.remove";' in social
assert 'f.TheirFlags & 1' in social
assert 'm_Friends.Delete(owner, target.ToString());' in social

for needle in (
  "if (!profile.PublishProfile)",
  "NexScopes.ProfileRead",
  "principal.HasScope(NexScopes.AdminAll)",
  '"profile_not_found"',
  "Get(account, request, response);"
):
    assert needle in profile, "missing private-profile access guard: "+needle

for needle in (
    '"/api/v1/messages"',
    "NexScopes.RelationshipsWrite",
    "owner != actorAccount.PrincipalID",
    "confirmed_friendship_required",
    "recipient_not_found",
    "Encoding.UTF8.GetByteCount(body) > 1024",
    "InstantMessageDialog.MessageFromAgent",
    "IncomingInstantMessage(im)",
    '["bytes"] = Encoding.UTF8.GetByteCount(body).ToString()',
):
    assert needle in im, "missing citizen IM safety contract: "+needle

assert "new NexCitizenImApi(" in connector
assert '"/api/v1/messages"' in connector
for needle in (
    '["/api/v1/profiles/{principalId}"]',
    '["/api/v1/relationships/{principalId}"]',
    '["/api/v1/relationships/{principalId}/{targetId}"]',
    '["/api/v1/relationships/{principalId}/blocks/{targetId}"]',
    '["/api/v1/messages"]',
    "CitizenAvatarProfileResponse",
    "CitizenFriendListResponse",
    "CitizenInstantMessageRequest",
    "CitizenInstantMessageResponse",
):
    assert needle in schema, "missing OpenAPI contract: "+needle

assert "Display Name" in docs and "Gruppenmitteilungen" in docs
assert "Chat-Historie" in docs
print("Citizen portal social/profile scopes, friend invitations, private profiles, IM and OpenAPI: OK")
