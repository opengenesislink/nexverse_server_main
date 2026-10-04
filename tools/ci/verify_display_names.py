#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
errors = []

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def require(path, *needles):
    text = read(path)
    for needle in needles:
        if needle not in text:
            errors.append(f"{path}: missing display-name marker: {needle}")

require(
    "OpenSim/Services/Interfaces/IUserAccountService.cs",
    "public string DisplayName = string.Empty;",
    "public int DisplayNameChanged;",
    "public string Username",
    "public string DefaultDisplayName",
    "public string EffectiveDisplayName",
    "public bool IsDisplayNameDefault",
    "public static class DisplayNamePolicy",
    "public const int MaximumLength = 31;",
    "public const int ChangeIntervalSeconds = 7 * 24 * 60 * 60;",
)

require(
    "OpenSim/Services/UserAccountService/UserAccountService.cs",
    'd.Data.ContainsKey("DisplayName")',
    'd.Data.ContainsKey("DisplayNameChanged")',
    'd.Data["DisplayName"] = data.DisplayName',
    'd.Data["DisplayNameChanged"] = data.DisplayNameChanged.ToString()',
)

for path, version in [
    ("OpenSim/Data/MySQL/Resources/UserAccount.migrations", ":VERSION 8"),
    ("OpenSim/Data/PGSQL/Resources/UserAccount.migrations", ":VERSION 7"),
    ("OpenSim/Data/SQLite/Resources/UserAccount.migrations", ":VERSION 5"),
]:
    require(
        path,
        version,
        "DisplayName",
        "DisplayNameChanged",
    )

require(
    "NexVerse/Core/Identity/INexUserService.cs",
    "public string DisplayName { get; }",
    "public bool IsDisplayNameDefault { get; }",
    "public int DisplayNameChanged { get; }",
    "public int DisplayNameNextUpdate { get; }",
    "NexUserRecord SetDisplayName(",
)

require(
    "NexVerse/Server/Api/NexVerseUserApi.cs",
    "public NexUserRecord SetDisplayName(",
    "DisplayNamePolicy.TryNormalize",
    '"display_name_cooldown"',
    'response.AddHeader(',
    '"display_name"',
    '"is_display_name_default"',
    '"display_name_changed"',
    '"display_name_next_update"',
    '["username"] = UserName(user)',
    '"user.updated"',
)

require(
    "OpenSim/Region/ClientStack/Linden/Caps/BunchOfCaps/BunchOfCaps.cs",
    'RegisterSimpleHandler("GetDisplayNames"',
    'RegisterSimpleHandler("SetDisplayName"',
    "public void GetDisplayNames(",
    "public void SetDisplayName(",
    "ScenePresence sp = m_Scene.GetScenePresence(m_AgentID);",
    "if (sp == null || sp.IsDeleted)",
    'map.TryGetValue("display_name"',
    "displayNameValue is OSDArray changeArray",
    '"SetDisplayNameReply"',
    '"DisplayNameUpdate"',
    "BuildDisplayNameAgent(account)",
    'httpResponse.ContentType = "application/llsd+xml";',
    "httpResponse.RawBuffer =",
    '"display_name_expires"',
    "account.EffectiveDisplayName",
    "account.Username",
)

for path in (
    "bin/OpenSimDefaults.ini",
    "bin/OpenSim.ini",
    "bin/OpenSim.ini.example",
):
    require(
        path,
        'Cap_GetDisplayNames = "localhost"',
        'Cap_SetDisplayName = "localhost"',
    )

require(
    "OpenSim/Services/LLLoginService/LLLoginResponse.cs",
    'responseData["username"] = username;',
    'responseData["display_name"] = displayName;',
    'responseData["is_display_name_default"] = isDisplayNameDefault;',
    'map["username"] = OSD.FromString(username);',
    'map["display_name"] = OSD.FromString(displayName);',
)

require(
    "OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs",
    "public LSL_String llGetDisplayName(LSL_Key id)",
    "return account.EffectiveDisplayName;",
    "public LSL_Key llRequestDisplayName(LSL_Key id)",
    "localAccount.EffectiveDisplayName",
)

require(
    "NexVerse/Server/Api/NexVerseWorldApiHandlers.cs",
    '["display_name"] = new { type = "string", maxLength = 31 }',
    '["is_display_name_default"] = new { type = "boolean" }',
    '["display_name_changed"] = new { type = "integer", minimum = 0 }',
    '["display_name_next_update"] = new { type = "integer", minimum = 0 }',
    'title = "Persistente Display-Name-Grundlage"',
)

require(
    "doc/NexVerse/ROADMAP.md",
    "Foundation status implemented during the 0.9.3.3 development line:",
    "same-region viewer caches receive a",
    "Remaining propagation work before section 7.2 is complete:",
)

require(
    "doc/NexVerse/DISPLAY_NAMES.md",
    "NexVerse Display Names",
    "seven-day cooldown",
    "SetDisplayNameReply",
    "DisplayNameUpdate",
    "llGetDisplayName",
    "llRequestDisplayName",
)

if errors:
    print("[NX-DISPLAY-NAMES] FAILED")
    for error in errors:
        print(" - " + error)
    raise SystemExit(1)

print("NexVerse display-name foundation: OK")

# Firestorm consumes SetDisplayNameReply/DisplayNameUpdate as canonical LLSD
# event envelopes from EventQueue, not as a synchronous SetDisplayName result.
require(
    "OpenSim/Region/ClientStack/Linden/Caps/BunchOfCaps/BunchOfCaps.cs",
    '["message"] = OSD.FromString("SetDisplayNameReply")',
    '["message"] = OSD.FromString("DisplayNameUpdate")',
    "eventQueue.Enqueue((OSD)reply, m_AgentID)",
)
