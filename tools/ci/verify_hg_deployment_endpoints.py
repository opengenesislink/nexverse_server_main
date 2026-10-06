#!/usr/bin/env python3
from pathlib import Path

opensim = Path("bin/OpenSim.ini").read_text(encoding="utf-8")
opensim_example = Path("bin/OpenSim.ini.example").read_text(encoding="utf-8")
robust_hg = Path("bin/Robust.HG.ini").read_text(encoding="utf-8")
robust_hg_example = Path("bin/Robust.HG.ini.example").read_text(encoding="utf-8")

expected_profile = 'ProfileServiceURL = ${Const|BaseURL}:${Const|PublicPort}'
for name, text in (("bin/OpenSim.ini", opensim), ("bin/OpenSim.ini.example", opensim_example)):
    assert expected_profile in text, f"{name}: UserProfiles must use the configured Robust public port"
    assert 'ProfileServiceURL = ${Const|BaseURL}:8002' not in text, f"{name}: stale profile port 8002"

assert 'BaseHostname = "hg.stadt-nexverse.de"' in robust_hg
assert 'PublicPort = "80"' in robust_hg
login_section = robust_hg.split("\n[LoginService]\n", 1)[1].split("\n[", 1)[0]
assert 'Currency = "NV$"' in login_section, "Firestorm login currency must be NV$"
assert 'UserProfilesServiceConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:UserProfilesConnector"' in robust_hg

profile_section = robust_hg.split("\n[UserProfilesService]\n", 1)[1].split("\n[", 1)[0]
assert "Enabled = true" in profile_section
assert 'LocalServiceModule = "OpenSim.Services.UserProfilesService.dll:UserProfilesService"' in profile_section

discovery_section = robust_hg.split("\n[NexDiscovery]\n", 1)[1].split("\n[", 1)[0]
assert 'TeleportBaseUri = "hop://${Const|BaseHostname}"' in discovery_section
assert "login.mynexverse.de" not in discovery_section

assert 'hop://hg.stadt-nexverse.de' in robust_hg_example

print("OpenGenesisLINK HG deployment endpoints: OK")
