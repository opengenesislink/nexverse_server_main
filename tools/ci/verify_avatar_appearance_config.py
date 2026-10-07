#!/usr/bin/env python3
from pathlib import Path

EXPECTED = {
    "PersistBakedTextures": "true",
    "ReuseTextures": "true",
    "DelayBeforeAppearanceSend": "1",
    "DelayBeforeAppearanceSave": "3",
    "ResendAppearanceUpdates": "false",
}

for path in ("bin/OpenSim.ini", "bin/OpenSim.ini.example"):
    content = Path(path).read_text(encoding="utf-8")
    assert "\n[Appearance]\n" in content, f"{path}: missing [Appearance] section"

    section = content.split("\n[Appearance]\n", 1)[1].split("\n[", 1)[0]

    for key, value in EXPECTED.items():
        marker = f"{key} = {value}"
        assert marker in section, f"{path}: missing {marker}"

    xbakes_index = content.index("\n[XBakes]\n")
    appearance_index = content.index("\n[Appearance]\n")
    assert appearance_index < xbakes_index, f"{path}: [Appearance] should be declared before [XBakes]"

print("OpenGenesisLINK avatar appearance cache configuration: OK")
