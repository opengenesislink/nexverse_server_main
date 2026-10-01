#!/usr/bin/env python3
"""Reject regressions back to the legacy World API AuthenticationService bearer bridge."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]

user_api = (ROOT / "NexVerse/Server/Api/NexVerseUserApi.cs").read_text(encoding="utf-8-sig")
connector = (ROOT / "NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8-sig")
handlers = (ROOT / "NexVerse/Server/Api/NexVerseWorldApiHandlers.cs").read_text(encoding="utf-8-sig")
identity = (ROOT / "NexVerse/Core/Identity/INexUserService.cs").read_text(encoding="utf-8-sig")
roadmap = (ROOT / "doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8-sig")

errors = []

required_user_api = (
    '"/api/v1/auth/session"',
    "HandleNativeSessionLogin",
    "VerifyPassword(",
    '"auth.session.login"',
    '"auth.session.created"',
    '"Cache-Control",',
    '"no-store"',
)
for token in required_user_api:
    if token not in user_api:
        errors.append(f"NexVerseUserApi.cs missing native auth cutover marker: {token}")

for forbidden in (
    '"/api/v1/auth/token"',
    "TryAuthenticateLegacyExchange",
    "TryAuthenticateLegacyToken",
    '"X-NexVerse-Principal"',
):
    if forbidden in user_api:
        errors.append(f"NexVerseUserApi.cs still contains retired legacy auth bridge marker: {forbidden}")

if "IAuthenticationService authentication," in connector.split("NexApiAuthenticator authenticator = new NexApiAuthenticator(", 1)[-1].split(");", 1)[0]:
    errors.append("NexApiAuthenticator is still wired to IAuthenticationService")

for token in (
    '["/api/v1/auth/session"]',
    '"ResidentSessionRequest"',
    '"ResidentSessionResponse"',
    "CredentialPostOperation",
):
    if token not in handlers:
        errors.append(f"OpenAPI contract missing native resident session marker: {token}")

if '["/api/v1/auth/token"]' in handlers:
    errors.append("OpenAPI still advertises retired legacy token exchange")

if "bool VerifyPassword(string principalId, string password);" not in identity:
    errors.append("INexUserService does not expose password verification for native session bootstrap")

if "[x] NexVerse-native resident sessions plus OIDC/scoped token issuance fully replace" not in roadmap:
    errors.append("Roadmap does not mark native auth cutover complete")

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print("NexVerse native auth cutover verified: resident session + native bearer/OIDC/API-key only; legacy bearer bridge retired.")
