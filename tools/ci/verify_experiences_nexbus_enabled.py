#!/usr/bin/env python3
"""Regression guard for production NexBus, Experiences and admin-only World API login."""
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[2]
ENV="$"+"{Environment|"

def contents(rel):
    return (ROOT/rel).read_text(encoding="utf-8")

def ini(rel, name):
    content=contents(rel)
    marker="["+name+"]"
    assert content.count(marker)==1,(rel,name)
    return content.split(marker,1)[1].split("\n[",1)[0]

for rel in ("bin/OpenSim.ini", "bin/OpenSim.ini.example"):
    agent=ini(rel,"NexVerseNodeAgent")
    exp=ini(rel,"NexExperiencesViewer")
    for name,entry in (("NodeAgent",agent),("ExperiencesViewer",exp)):
        assert re.search(r"(?m)^\s*Enabled\s*=\s*true\s*$",entry),(rel,name)
    assert 'SharedKey = "'+ENV+'NEXVERSE_NEXBUS_SHARED_KEY}"' in agent
    assert 'PeerUrl = "'+ENV+'NEXVERSE_NEXBUS_PEER_URL}"' in agent
    assert 'ApiKey = "'+ENV+'NEXVERSE_EXPERIENCES_API_KEY}"' in exp
    assert 'WorldApiBaseUrl = "https://world.stadt-nexverse.de"' in exp
    assert "http://hg.stadt-nexverse.de/internal/nexbus/v1/events" not in agent

for rel in ("bin/Robust.HG.ini","bin/Robust.HG.ini.example"):
    bus=ini(rel,"NexBus")
    assert re.search(r"(?m)^\s*Enabled\s*=\s*true\s*$",bus),rel
    assert 'SharedKey = "'+ENV+'NEXVERSE_NEXBUS_SHARED_KEY}"' in bus
    assert 'Peers = "'+ENV+'NEXVERSE_NEXBUS_PEERS}"' in bus

user=contents("NexVerse/Server/Api/NexVerseUserApi.cs")
admin_route='"/api/v1/auth/admin/session"'
assert admin_route in user
login=user.split("private void HandleNativeSessionLogin(",1)[1].split("private void HandleAuditSearch(",1)[0]
assert "bool requireAdmin = false" in login
assert "if (requireAdmin &&" in login
assert "user.UserLevel < Math.Max(200, m_AdminMinimumLevel)" in login
assert "NexScopes.AdminAll" in login
assert login.index("if (requireAdmin &&") < login.index("m_Tokens.Issue(")
assert '"administrator_required"' in login
assert "m_Security.IsTotpEnabled" in login
me=user.split("private void HandleAdminSessionInfo(",1)[1].split("private void HandleNativeSessionLogin(",1)[0]
assert "NexScopes.AdminAll" in me
assert "account.UserLevel < Math.Max(200, m_AdminMinimumLevel)" in me
assert "account == null" in me
assert '"Cache-Control", "no-store"' in me

store=contents("NexVerse/Core/Security/NexApiKeyStore.cs")
assert "NexScopes.ExperiencesScript" in store
api=contents("NexVerse/Server/Api/NexVerseWorldApiHandlers.cs")
assert admin_route in api
ui=contents("NexVerse/Server/Api/NexApiDocsPage.cs")
for marker in ('data-page="adminlogin"','id="page-adminlogin"','id="adminLoginPassword"',
               "'/api/v1/auth/admin/session'","function adminIsActive()", "function adminLogout()",
               "'secrets','nodes','grid'", 'id="page-secrets"',
               "'/api/v1/auth/api-keys'","window.crypto.getRandomValues(bytes)"):
    assert marker in ui,marker
assert "localStorage.setItem(" not in ui
assert "sessionStorage.setItem(" not in ui
assert 'id="secretAdminPassword"' not in ui, "secrets must use shared admin session"

# Browser script is embedded in C# HTML raw string; parse syntax using Node.js when installed.
match=re.search(r"<script>\s*(.*?)\s*</script>",ui,re.S)
assert match,"Control Center JavaScript not found"
node=shutil.which("node")
if node:
    with tempfile.TemporaryDirectory() as directory:
        file=Path(directory)/"world-api-docs.js"
        file.write_text(match.group(1),encoding="utf-8")
        subprocess.run([node,"--check",str(file)],check=True)
else:
    print("Hinweis: Node.js nicht vorhanden; JS-Syntaxpruefung uebersprungen")

print("OpenGenesisLINK NexBus + Experiences + admin-only login contract: OK")
