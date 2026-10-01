#!/usr/bin/env python3
"""Fail CI when a threat-checked OSSL function lacks an enabled policy rule."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
API = ROOT / "OpenSim/Region/ScriptEngine/Shared/Api/Implementation/OSSL_Api.cs"
DEFAULT = ROOT / "bin/config-include/osslDefaultEnable.ini"
OVERRIDE = ROOT / "bin/config-include/osslEnable.ini"

FUNCTION_RE = re.compile(
    r'CheckThreatLevel\(ThreatLevel\.[A-Za-z]+,\s*"([^"]+)"\)'
)

functions = set(FUNCTION_RE.findall(API.read_text(encoding="utf-8-sig")))
rules = {}

for path in (DEFAULT, OVERRIDE):
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("#") or "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        if not key.startswith("Allow_os"):
            continue

        name = key[len("Allow_"):].strip()
        value = value.split(";", 1)[0].strip()
        rules[name] = value

errors = []

if not functions:
    errors.append("No threat-checked OSSL functions were discovered; parser regression likely.")

missing = sorted(functions - rules.keys())
disabled = sorted(
    name for name in functions
    if rules.get(name, "").lower() == "false"
)

if missing:
    errors.append("Missing OSSL policy rules: " + ", ".join(missing))
if disabled:
    errors.append(
        "Current OSSL functions disabled by effective policy: " +
        ", ".join(disabled)
    )

profile = OVERRIDE.read_text(encoding="utf-8-sig")
required_profile_lines = (
    "AllowOSFunctions = true",
    "AllowMODFunctions = true",
    "AllowLightShareFunctions = true",
    "OSFunctionThreatLevel = NoAccess",
)
for line in required_profile_lines:
    if line not in profile:
        errors.append(
            "Required NexVerse OSSL profile setting missing: " + line
        )

compatibility_open_functions = (
    "osGetNotecard",
    "osGetNotecardLine",
    "osGetNumberOfNotecardLines",
    "osAvatarPlayAnimation",
    "osAvatarStopAnimation",
)
for name in compatibility_open_functions:
    effective = rules.get(name, "")
    if effective.lower() != "true":
        errors.append(
            f"OSSL compatibility function {name} must be explicitly true; "
            f"effective value is {effective or '<missing>'}"
        )

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print(
    f"NexVerse OSSL policy covers {len(functions)} threat-checked functions; "
    "all are enabled by explicit effective rules."
)
