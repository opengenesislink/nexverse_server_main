#!/usr/bin/env python3
from pathlib import Path

security = Path("NexVerse/Core/Security/NexSecurity.cs").read_text(encoding="utf-8")
identity = Path("NexVerse/Core/Identity/INexUserService.cs").read_text(encoding="utf-8")
api = Path("NexVerse/Server/Api/NexVerseUserApi.cs").read_text(encoding="utf-8")
oauth = Path("NexVerse/Server/Api/NexOAuthApi.cs").read_text(encoding="utf-8")
account = Path("OpenSim/Services/Interfaces/IUserAccountService.cs").read_text(encoding="utf-8")
mysql = Path("OpenSim/Data/MySQL/Resources/UserAccount.migrations").read_text(encoding="utf-8")
pgsql = Path("OpenSim/Data/PGSQL/Resources/UserAccount.migrations").read_text(encoding="utf-8")
sqlite = Path("OpenSim/Data/SQLite/Resources/UserAccount.migrations").read_text(encoding="utf-8")

for marker in (
    'public const string Resident = "resident";',
    'public const string Support = "support";',
    'public const string Moderator = "moderator";',
    'public const string RegionManager = "region_manager";',
    'public const string Administrator = "administrator";',
    "TryNormalizeAssignment(",
    "GetEffectiveScopes(",
    "NexScopes.SecurityManage",
    "NexScopes.AdminAll",
    "administrator_role_requires_admin",
    "privileged_scope_requires_admin",
):
    assert marker in security, f"missing authorization policy marker: {marker}"

for marker in (
    "IReadOnlyList<string> Roles",
    "IReadOnlyList<string> ExplicitScopes",
    "SetAuthorization(",
):
    assert marker in identity, f"missing user authorization model marker: {marker}"

for marker in (
    "NexVerseRoles",
    "NexVerseScopes",
):
    assert marker in account, f"missing UserAccount authorization persistence: {marker}"
    assert marker in mysql, f"missing MySQL authorization migration: {marker}"
    assert marker in pgsql, f"missing PostgreSQL authorization migration: {marker}"
    assert marker in sqlite, f"missing SQLite authorization migration: {marker}"

for marker in (
    '"/api/v1/auth/authorization-model"',
    '"authorization"',
    "HandleAuthorizationModel(",
    "HandleUserAuthorization(",
    "NexScopes.SecurityManage",
    "admin_target_requires_admin",
    "reauthentication_required = true",
    '"users.authorization.update"',
    '"user.authorization.changed"',
    "RevokeSubjectRefreshTokens(",
    "account.NexVerseStateChanged = Math.Max(",
):
    assert marker in api, f"missing World API authorization marker: {marker}"

for marker in (
    "NexAuthorizationPolicy.GetEffectiveScopes(",
    "user.Roles",
    "user.ExplicitScopes",
    "NexScopes.RegionsRead",
    "NexScopes.SecurityManage",
):
    assert marker in oauth, f"missing OAuth role/scope marker: {marker}"

# Legacy UserLevel changes must also invalidate bearer tokens.
level_method = api.split("public bool SetUserLevel", 1)[1].split("public NexUserRecord SetAuthorization", 1)[0]
assert "NexVerseStateChanged" in level_method

print("User role/scope authorization contract: OK")
