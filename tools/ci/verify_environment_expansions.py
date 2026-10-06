#!/usr/bin/env python3
"""Ensure runtime ${Environment|KEY} references are declared before Nini expansion."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
REF = re.compile(r"\$\{Environment\|([^}]+)\}")
SECTION = re.compile(r"^\s*\[([^]]+)\]\s*$")
ASSIGN = re.compile(r"^\s*([A-Z0-9_]+)\s*=")


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8", errors="replace")


def refs(paths):
    found = set()
    for rel in paths:
        found.update(REF.findall(read(rel)))
    return found


def declarations(paths):
    found = set()
    for rel in paths:
        section = ""
        for raw in read(rel).splitlines():
            m = SECTION.match(raw)
            if m:
                section = m.group(1)
                continue
            if section != "Environment":
                continue
            m = ASSIGN.match(raw)
            if m:
                found.add(m.group(1))
    return found


profiles = [
    (
        "simulator",
        ["bin/OpenSim.ini", "bin/config-include/GridCommon.ini"],
        ["bin/OpenSim.ini", "bin/config-include/GridCommon.ini"],
    ),
    (
        "simulator-example",
        ["bin/OpenSim.ini.example", "bin/config-include/GridCommon.ini"],
        ["bin/OpenSim.ini.example", "bin/config-include/GridCommon.ini"],
    ),
    (
        "robust-hg",
        ["bin/Robust.HG.ini"],
        ["bin/Robust.HG.ini"],
    ),
    (
        "robust-hg-example",
        ["bin/Robust.HG.ini.example"],
        ["bin/Robust.HG.ini.example"],
    ),
]

violations = []
for name, reference_paths, declaration_paths in profiles:
    required = refs(reference_paths)
    declared = declarations(declaration_paths)
    missing = sorted(required - declared)
    if missing:
        violations.append((name, missing))

if violations:
    print("[NX-SECRET-0002] Environment expansion declarations are incomplete:")
    for profile, missing in violations:
        print(f" - {profile}: missing {', '.join(missing)}")
    raise SystemExit(1)

print("NexVerse environment expansion declaration guard: OK")
