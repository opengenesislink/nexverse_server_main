#!/usr/bin/env python3
from pathlib import Path

roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
groups_service = Path("OpenSim/Addons/Groups/Service/GroupsService.cs").read_text(encoding="utf-8")
groups_messaging = Path("OpenSim/Addons/Groups/GroupsMessagingModule.cs").read_text(encoding="utf-8")
groups_hg = Path("OpenSim/Addons/Groups/Hypergrid/GroupsServiceHGConnector.cs").read_text(encoding="utf-8")
groups_api = Path("NexVerse/Server/Api/NexGroupsApi.cs").read_text(encoding="utf-8")
experience_store = Path("NexVerse/Core/Experiences/NexExperienceStore.cs").read_text(encoding="utf-8")
experience_api = Path("NexVerse/Server/Api/NexExperiencesApi.cs").read_text(encoding="utf-8")
experience_module = Path("NexVerse/RegionModules/Experiences/NexExperienceModule.cs").read_text(encoding="utf-8")
experience_interface = Path("OpenSim/Region/Framework/Interfaces/IExperienceModule.cs").read_text(encoding="utf-8")
lsl_interface = Path("OpenSim/Region/ScriptEngine/Shared/Api/Interface/ILSL_Api.cs").read_text(encoding="utf-8")
lsl_api = Path("OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs").read_text(encoding="utf-8")
lsl_stub = Path("OpenSim/Region/ScriptEngine/Shared/Api/Runtime/LSL_Stub.cs").read_text(encoding="utf-8")
lsl_constants = Path("OpenSim/Region/ScriptEngine/Shared/Api/Runtime/LSL_Constants.cs").read_text(encoding="utf-8")
scene_object = Path("OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs").read_text(encoding="utf-8")
event_handlers = Path("OpenSim/Region/ScriptEngine/YEngine/MMRIEventHandlers.cs").read_text(encoding="utf-8")
event_codes = Path("OpenSim/Region/ScriptEngine/YEngine/MMRScriptEventCode.cs").read_text(encoding="utf-8")
event_map = Path("OpenSim/Region/ScriptEngine/YEngine/XMRInstMain.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
security = Path("NexVerse/Core/Security/NexSecurity.cs").read_text(encoding="utf-8")
opensim_config = Path("bin/OpenSim.ini.example").read_text(encoding="utf-8")
robust_config = Path("bin/Robust.ini.example").read_text(encoding="utf-8")
robust_hg_config = Path("bin/Robust.HG.ini.example").read_text(encoding="utf-8")
regression = Path("tools/ci/NexExperiencesRegression/Program.cs").read_text(encoding="utf-8")

for marker in (
    "### 11.1 NexGroups",
    "### 11.2 Experiences",
    "### 11.3 Experience LSL",
):
    assert marker in roadmap, f"missing chapter 11 roadmap marker: {marker}"

# Historical transport remains in place until the parity gate is complete.
for marker in (
    "public class GroupsService",
    "CreateGroup(",
    "GetGroupMembers(",
    "AddGroupRole(",
    "AddAgentToGroupInvite(",
    "AddGroupNotice(",
    "GetGroupNotices(",
    "AddGroupBan(",
    "RemoveGroupBan(",
    "DeleteGroup(",
    "m_NexModeration.IsBanned(",
):
    assert marker in groups_service, f"missing NexGroups service parity marker: {marker}"

for marker in (
    "class GroupsMessagingModule",
    "StartGroupChatSession(",
    "SendMessageToGroup(",
):
    assert marker in groups_messaging, f"group chat compatibility lost: {marker}"

assert "class GroupsServiceHGConnector" in groups_hg, "Hypergrid groups connector was removed"

for marker in (
    "/api/v1/groups",
    '"members"',
    '"roles"',
    '"invites"',
    '"notices"',
    '"bans"',
    '"accounting"',
    '"capabilities"',
    "EnsureWalletAccount(",
    "NexLedgerAccountClass.Group",
    "GroupsDataUtils.GroupMembersData",
    "GroupsDataUtils.GroupRolesData",
):
    assert marker in groups_api, f"missing NexGroups API marker: {marker}"

for marker in (
    "NexExperience",
    "OwnerId",
    "GroupId",
    "Admins",
    "Contributors",
    "AllowedResidents",
    "BlockedResidents",
    "AllowedEstates",
    "BlockedEstates",
    "AllowedParcels",
    "BlockedParcels",
    "ScriptBindings",
    "KeyValues",
    "GetLogs(",
    "MaxStoreBytes",
    "UpdateKeyValue(",
    "checkOriginal",
    "retryMismatch",
    'throw new InvalidOperationException("Experience is disabled.");',
):
    assert marker in experience_store, f"missing Experience core marker: {marker}"

for marker in (
    "/api/v1/experiences",
    "/api/v1/experiences/script/resolve",
    "/api/v1/experiences/script/details",
    "/api/v1/experiences/script/permission",
    "/api/v1/experiences/script/location",
    "/api/v1/experiences/script/kv",
    "NexScopes.ExperiencesScript",
    "experience.created",
    "experience.permission.updated",
    "experience.script.bound",
):
    assert marker in experience_api, f"missing Experience API marker: {marker}"

# User-supplied owner/actor IDs must never let scoped service credentials
# impersonate residents without the admin:* authority.
assert experience_api.count("account == null && !principal.HasScope(NexScopes.AdminAll)") >= 2
assert "if (!principal.HasScope(NexScopes.AdminAll))" in experience_api
assert "Experience management as another resident requires admin:*." in experience_api

for forbidden in (
    "DbConnection",
    "MySql",
    "Npgsql",
    "SQLite",
):
    assert forbidden not in experience_module, f"simulator Experience adapter opens storage directly: {forbidden}"

for marker in (
    "IExperienceModule",
    "ResolveExperience(",
    "HasExperiencePermission(",
    "CreateKeyValue(",
    "ReadKeyValue(",
    "UpdateKeyValue(",
    "DeleteKeyValue(",
    "GetKeyValueStats(",
    "ListKeyValueKeys(",
):
    assert marker in experience_interface, f"missing Experience region contract marker: {marker}"
    assert marker in experience_module, f"missing Experience region adapter marker: {marker}"

lsl_functions = (
    "llRequestExperiencePermissions",
    "llGetExperienceDetails",
    "llAgentInExperience",
    "llCreateKeyValue",
    "llReadKeyValue",
    "llUpdateKeyValue",
    "llDeleteKeyValue",
    "llDataSizeKeyValue",
    "llKeyCountKeyValue",
    "llKeysKeyValue",
    "llGetExperienceErrorMessage",
)

for marker in lsl_functions:
    assert marker in lsl_interface, f"missing LSL interface function: {marker}"
    assert marker in lsl_api, f"missing LSL implementation function: {marker}"
    assert marker in lsl_stub, f"missing LSL runtime stub function: {marker}"

assert "llUpdateKeyValue(LSL_String key, LSL_String value, LSL_Integer checkedFlag, LSL_String originalValue)" in lsl_interface
assert "XP_ERROR_NO_EXPERIENCE = 5" in lsl_constants
assert "XP_ERROR_STORAGE_EXCEPTION = 13" in lsl_constants
assert "XP_ERROR_KEY_NOT_FOUND = 14" in lsl_constants
assert "XP_ERROR_RETRY_UPDATE = 15" in lsl_constants
assert "XP_ERROR_NOT_PERMITTED_LAND = 17" in lsl_constants

for marker in (
    "experience_permissions = 1UL << 26",
    "experience_permissions_denied = 1UL << 27",
):
    assert marker in scene_object, f"missing Experience event flag: {marker}"

for marker in (
    "void experience_permissions(string agent_id);",
    "void experience_permissions_denied(string agent_id, int reason);",
):
    assert marker in event_handlers, f"missing YEngine Experience handler: {marker}"

for marker in (
    "experience_permissions = 26",
    "experience_permissions_denied = 27",
):
    assert marker in event_codes, f"missing YEngine Experience event code: {marker}"

for marker in (
    '{"experience_permissions", ScriptEventCode.experience_permissions}',
    '{"experience_permissions_denied", ScriptEventCode.experience_permissions_denied}',
):
    assert marker in event_map, f"missing YEngine Experience event map: {marker}"

for marker in (
    "GroupsRead",
    "GroupsManage",
    "ExperiencesRead",
    "ExperiencesManage",
    "ExperiencesScript",
):
    assert marker in security, f"missing chapter 11 scope: {marker}"

for marker in (
    '"/api/v1/groups"',
    '"/api/v1/experiences"',
    "new NexGroupsApi(",
    "new NexExperiencesApi(",
):
    assert marker in connector, f"missing chapter 11 API registration: {marker}"

assert "[NexExperiencesViewer]" in opensim_config
assert "FirestormReadCaps = false" in opensim_config
assert "FirestormPermissionCaps = false" in opensim_config
assert "ScriptPendingConsent = false" in opensim_config
assert "NativeExperiencePrompt = false" in opensim_config
assert "NativeExperiencePrompt requires ScriptPendingConsent" in experience_module
assert "IExperienceQuestionClient client" in experience_module
assert "client.SendExperienceQuestion(" in experience_module
assert "if (!nativePromptSent)" in experience_module
assert "ResolveViewerPendingConsent" in experience_module
native_client = Path("OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs").read_text(encoding="utf-8")
native_interface = Path("OpenSim/Framework/IClientAPI.cs").read_text(encoding="utf-8")
for marker in (
    "interface IExperienceQuestionClient",
    "bool SendExperienceQuestion(",
):
    assert marker in native_interface, marker
for marker in (
    "IExperienceQuestionClient",
    "scriptQuestion.Experience = new ScriptQuestionPacket.ExperienceBlock",
    "ExperienceID = experienceId",
    "scriptQuestion.Data.Questions = question;",
    "OutPacket(scriptQuestion, ThrottleOutPacketType.Task);",
):
    assert marker in native_client, f"native Firestorm UDP prompt missing: {marker}"
assert "ScriptQuestionPacket" in native_client
assert "const int experiencePermissions = 0x2000;" in experience_module
assert "0x04 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000" not in experience_module
assert "consentClient.OnScriptAnswer += nativeAnswerHandler" in experience_module
assert "consentClient.OnScriptAnswer -= nativeAnswerHandler" in experience_module
assert "answer != 0" in experience_module
assert "CompleteRequests(m_Pending.TakeSpecific(" in experience_module
assert "public NexPendingExperienceRequest[] TakeSpecific(" in experience_store
assert '"one script Deny drained another script\'s request"' in regression
for marker in (
    "public NexPendingExperienceRequest[] CancelResident(",
    "public NexPendingExperienceRequest[] CancelScript(",
    "public Action Cancel { get; init; }",
):
    assert marker in experience_store, f"missing pending consent lifecycle: {marker}"
for marker in (
    "OnScriptReset += lifecycle.Reset",
    "OnRemoveScript += lifecycle.RemoveScript",
    "OnRemovePresence += lifecycle.RemovePresence",
    "OnMakeChildAgent += lifecycle.MakeChild",
    "CancelPendingSilently(",
    "m_Pending.CancelScript(",
    "m_Pending.CancelResident(",
    "OnScriptReset -= lifecycle.Reset",
    "OnRemoveScript -= lifecycle.RemoveScript",
    "OnRemovePresence -= lifecycle.RemovePresence",
    "OnMakeChildAgent -= lifecycle.MakeChild",
):
    assert marker in experience_module, f"pending consent lifecycle guard missing: {marker}"
assert "native listener cleanup emitted a stale LSL event" in regression



for marker in (
    "NexPendingExperienceQueue",
    "NexPendingExperienceRequest",
    "TryAdd(",
    "Take(Guid resident, Guid experience)",
    "CancelRegion(",
    "public int Count",
):
    assert marker in experience_store, f"missing bounded pending consent contract: {marker}"
for marker in (
    "QueueExperiencePermissionRequest(",
    "ScriptPendingConsent requires FirestormPermissionCaps",
    "FindLiveConsentContext",
    "ResolveViewerPendingConsent",
    "XP_ERROR_REQUEST_PERM_TIMEOUT",
    "m_Pending.CancelRegion",
    "m_Pending.Expire(",
):
    source = (lsl_constants if marker == "XP_ERROR_REQUEST_PERM_TIMEOUT" else experience_module)
    assert marker in source or marker in lsl_api, f"missing pending consent bridge: {marker}"
assert "QueueExperiencePermissionRequest(" in experience_interface
assert "module.QueueExperiencePermissionRequest(" in lsl_api
assert "Pending LSL consent never itself writes central permission" in regression
assert "ViewerPermissionsApiKey" in opensim_config
assert "GetExperiences" in experience_module
for cap in (
    '"AgentExperiences"',
    '"GetAdminExperiences"',
    '"GetCreatorExperiences"',
    '"GroupExperiences"',
):
    assert cap in experience_module, f"Firestorm role/group capability missing: {cap}"
assert "HandleViewerRoleList" in experience_module
assert 'role == "group"' in experience_module
assert '["experience_ids"] = ids' in experience_module
assert "if (req.HttpMethod != \"GET\")" in experience_module
assert '"/api/v1/experiences/viewer/group"' in experience_api
assert "NexScopes.ExperiencesViewerPermissions" in experience_api
assert "GetGroupExperiences(groupId)" in experience_api
assert "admin_ids = lists.Admin.Select" in experience_api
assert "contributor_ids = lists.Contributor.Select" in experience_api
assert 'm_Store.GetResidentLists(resident)' in experience_api
for marker in (
    "adminLists.Admin.Contains",
    "contributorLists.Contributor.Contains",
    "GetGroupExperiences(created.GroupId)",
    "Disabled experiences must disappear from Firestorm role/group lists",
):
    assert marker in regression, f"role-list regression missing: {marker}"
assert "ExperiencePreferences" in experience_module
assert "CreateViewerPermissionRequest" in experience_module
assert "if (!IsCurrentViewer(issuingScene, avatar, lease))" in experience_module
assert "m_CapLeases.IsCurrent(lease)" in experience_module
assert "m_CapLeases.Issue(" in experience_module
assert "m_CapLeases.InvalidateResident(" in experience_module
assert "m_CapLeases.InvalidateRegion(" in experience_module
assert "m_CapLeases.InvalidateAll()" in experience_module
lease_store = Path("NexVerse/Core/Experiences/NexExperienceCapLeaseRegistry.cs").read_text(encoding="utf-8")
for marker in ("public sealed class NexExperienceCapLeaseRegistry",
               "old.Revoke()", "current.Revoke()", "m_Active.Clear()",
               "ReferenceEquals(current, lease)", "lock (m_Sync)"):
    assert marker in lease_store, f"cap lease lifetime guard missing: {marker}"
assert "new login to same region must revoke original URL" in regression
assert "returning resident must never revive old URL" in regression
assert "RegisterFirestormReadCaps(Scene issuingScene, UUID avatar, Caps caps)" in experience_module
assert "TryGetScenePresence(avatar" in experience_module
assert "m_ViewerCapListeners[scene] = listener" in experience_module
assert "OnRegisterCaps -= listener" in experience_module
assert "!presence.IsChildAgent" in experience_module
assert "m_Scenes.Contains(issuingScene)" in experience_module
assert 'resident_id = avatar.ToString()' in experience_module
assert '"experiences:viewer:permissions"' in security or "ExperiencesViewerPermissions" in security
assert "experience.resident.consent" in experience_store
assert "SetOwnResidentPermission" in experience_store
assert "GetResidentLists" in experience_store
assert '"/api/v1/experiences/viewer/permissions"' in experience_api
assert "service_key_required" in experience_api
for marker in (
    '"GetExperienceInfo"',
    '"FindExperienceByName"',
    "RegisterFirestormReadCaps",
    "AddSimpleStreamHandler(info, true)",
    "IsCurrentViewer(issuingScene, avatar, lease)",
    "OSDParser.SerializeLLSDXmlString(result)",
):
    assert marker in experience_module, f"missing Firestorm read-only CAP contract: {marker}"
for marker in (
    '"/api/v1/experiences/script/info"',
    '"/api/v1/experiences/script/search"',
    "NexScopes.ExperiencesScript",
    "tokens.Length > 64",
):
    assert marker in experience_api, f"missing bounded Experience viewer metadata contract: {marker}"
assert "NEXVERSE_EXPERIENCES_API_KEY" in opensim_config
assert "NexModerationStorePath" in robust_config
assert "NexModerationStorePath" in robust_hg_config
assert "[NexExperiences]" in robust_config
assert "[NexExperiences]" in robust_hg_config

for marker in (
    "checked K/V retry semantics failed",
    "missing-key update did not create",
    "Experience persistence/reopen failed",
    "Experience deletion cleanup failed",
    "disabled Experience still allowed script K/V access",
):
    assert marker in regression, f"missing Experience regression marker: {marker}"

print("OpenGenesisLINK Chapter 11 NexGroups/NexExperiences contract: OK")
