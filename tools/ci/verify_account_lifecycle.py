#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def require(path, *needles):
    text = read(path)
    missing = [needle for needle in needles if needle not in text]
    if missing:
        raise SystemExit(f"{path}: missing lifecycle markers: {missing}")

require(
    "OpenSim/Services/Interfaces/IUserAccountService.cs",
    'public Boolean Active = true;',
    'public string NexVerseState = "active";',
    'public string NexVerseStateReason = string.Empty;',
    'public bool LoginAllowed =>'
)

require(
    "OpenSim/Services/LLLoginService/LLLoginService.cs",
    'if (!account.LoginAllowed)',
    'reason: NexVerse account state is {2}'
)

require(
    "NexVerse/Core/Identity/INexUserService.cs",
    'public const string Active = "active";',
    'public const string Locked = "locked";',
    'public const string Banned = "banned";',
    'public const string Deactivated = "deactivated";',
    'public const string Provisioning = "provisioning";',
    'public const string ProvisioningFailed = "provisioning_failed";'
)

require(
    "NexVerse/Server/Api/NexVerseUserApi.cs",
    'error = "account_blocked";',
    'account.NexVerseState = NexAccountStates.Provisioning;',
    'NexAccountStates.ProvisioningFailed',
    'HandleSetState(request, response, parts[0]);',
    'HandleDeactivate(request, response, parts[0]);',
    '"users.state.update"',
    '"user.state.changed"',
    '"users.deactivate"',
    '"user.deactivated"'
)

for path, version in [
    ("OpenSim/Data/MySQL/Resources/UserAccount.migrations", ":VERSION 8"),
    ("OpenSim/Data/PGSQL/Resources/UserAccount.migrations", ":VERSION 7"),
    ("OpenSim/Data/SQLite/Resources/UserAccount.migrations", ":VERSION 5"),
]:
    require(
        path,
        version,
        "UserCountry",
        "NexVerseState",
        "NexVerseStateReason",
        "NexVerseStateChanged"
    )

print("NexVerse account lifecycle policy: OK")
