#!/usr/bin/env python3
"""Fail CI when a threat-checked OSSL function lacks an enabled policy rule."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
API = ROOT / "OpenSim/Region/ScriptEngine/Shared/Api/Implementation/OSSL_Api.cs"
DEFAULT = ROOT / "bin/config-include/osslDefaultEnable.ini"
OVERRIDE = ROOT / "bin/config-include/osslEnable.ini"

FUNCTION_RE = re.compile(r'CheckThreatLevel\\(ThreatLevel\\.[A-Za-z]+,\\s*"([^"]+)"\\)')
RULE_RE = re.compile(r'^\\s*Allow_(os[A-Za-z0-9_]+)\\s*=\\s*(.*?)\\s*$', re.MULTILINE)

functions = set(FUNCTION_RE.findall(API.read_text(encoding="utf-8")))
rules = {}
for path in (DEFAULT, OVERRIDE):
    content = path.read_text(encoding="utf-8")
    for name, value in RULE_RE.findall(content):
        value = value.split(";", 1)[0].strip()
        rules[name] = value

missing = sorted(functions - rules.keys())
disabled = sorted(name for name in functions if rules.get(name, "").lower() == "false")

errors = []
if missing:
    errors.append("Missing OSSL policy rules: " + ", ".join(missing))
if disabled:
    errors.append("Current OSSL functions disabled by effective policy: " + ", ".join(disabled))

profile = OVERRIDE.read_text(encoding="utf-8")
required_profile_lines = (
    "AllowOSFunctions = true",
    "AllowMODFunctions = true",
    "AllowLightShareFunctions = true",
    "OSFunctionThreatLevel = NoAccess",
)
for line in required_profile_lines:
    if line not in profile:
        errors.append("Required NexVerse OSSL profile setting missing: " + line)

# These read-only helpers are widely used by established scripted products
# (including PMAC).  Merely being non-false is insufficient: estate-only rules
# still terminate ordinary YEngine scripts with an OSSL permission error.
compatibility_open_functions = (
    "osGetNotecard",
    "osGetNotecardLine",
    "osGetNumberOfNotecardLines",
)
for name in compatibility_open_functions:
    if rules.get(name, "").lower() != "true":
        errors.append(
            f"OSSL compatibility function {name} must be explicitly true; "
            f"effective value is {rules.get(name, '<missing>')}"
        )

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print(f"NexVerse OSSL policy covers {len(functions)} threat-checked functions; all are enabled by explicit effective rules.")
