#!/usr/bin/env python3
"""Verify the NexVerse browser OAuth login/consent surface remains wired and hardened."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]

oauth = (ROOT / "NexVerse/Server/Api/NexOAuthApi.cs").read_text(encoding="utf-8-sig")
page = (ROOT / "NexVerse/Server/Api/NexOAuthBrowserPage.cs").read_text(encoding="utf-8-sig")
connector = (ROOT / "NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8-sig")
roadmap = (ROOT / "doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8-sig")

errors = []

for token in (
    "HandleBrowserAuthorization",
    "TryBuildAuthorizationRequest",
    "RedirectAuthorizationError",
    "NexResidentNameResolver.TryResolveLoginInput",
    "VerifyPassword(",
    "NexAccountStates.Active",
    '"oauth.authorization.approve"',
):
    if token not in oauth:
        errors.append(f"NexOAuthApi.cs missing browser authorization marker: {token}")

for token in (
    "Anmelden und Zugriff erlauben",
    'method="post"',
    'name="decision"',
    'value="approve"',
    'value="deny"',
    'autocomplete="current-password"',
    "Content-Security-Policy",
    "frame-ancestors 'none'",
    "Cache-Control",
    "no-store",
):
    if token not in page:
        errors.append(f"NexOAuthBrowserPage.cs missing browser security/UI marker: {token}")

for forbidden in (
    'name="password" value=',
    "localStorage",
    "sessionStorage",
    "document.cookie",
):
    if forbidden in page:
        errors.append(f"NexOAuthBrowserPage.cs contains forbidden credential persistence marker: {forbidden}")

if "userService," not in connector or "adminMinimumLevel," not in connector:
    errors.append("OAuth router is not wired to resident verification/admin scope context")

if "[x] browserbasierte OAuth2/OIDC-Anmeldung" not in roadmap:
    errors.append("Roadmap does not mark browser OAuth login/consent complete")

if errors:
    for error in errors:
        print("::error::" + error)
    sys.exit(1)

print("NexVerse browser OAuth login/consent surface verified.")
