#!/usr/bin/env python3
"""Reject plaintext secrets in tracked active NexVerse runtime INI files."""

from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]

tracked = subprocess.check_output(
    ["git", "ls-files", "bin/**/*.ini", "bin/*.ini"],
    cwd=ROOT,
    text=True,
).splitlines()

allowed = ("", "***", "CHANGE_ME", "changeme")
violations = []

for rel in tracked:
    path = ROOT / rel
    if not path.is_file():
        continue

    section = ""
    for number, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("#"):
            continue

        section_match = re.match(r"^\[([^]]+)\]$", line)
        if section_match:
            section = section_match.group(1)
            continue

        if "=" not in line:
            continue

        key, value = (part.strip() for part in line.split("=", 1))
        value = value.strip('"')

        if key.lower() == "connectionstring":
            match = re.search(r"(?i)(?:^|;)\s*Password=([^;]*)", value)
            if match:
                password = match.group(1).strip()
                safe = (
                    password in allowed
                    or password.startswith("\${Environment|")
                    or (password.startswith("<") and password.endswith(">"))
                )
                if not safe:
                    violations.append(
                        f"{rel}:{number}: [{section}] ConnectionString contains a plaintext password"
                    )

        if key.lower() in {
            "robustcertpassword",
            "sharedkey",
            "nativetokensigningkey",
        }:
            safe = (
                value in allowed
                or value.startswith("\${Environment|")
                or (value.startswith("<") and value.endswith(">"))
            )
            if not safe:
                violations.append(
                    f"{rel}:{number}: [{section}] {key} contains a plaintext runtime secret"
                )

if violations:
    print("[NX-SECRET-0001] Plaintext runtime secrets detected:")
    for violation in violations:
        print(" - " + violation)
    raise SystemExit(1)

print("NexVerse runtime secret guard: OK")
