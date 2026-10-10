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
assert "ViewerPermissionsApiKey" in opensim_config
assert "GetExperiences" in experience_module
assert "ExperiencePreferences" in experience_module
assert "CreateViewerPermissionRequest" in experience_module
assert "if (!IsCurrentViewer(avatar))" in experience_module
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
    "IsCurrentViewer(avatar)",
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
