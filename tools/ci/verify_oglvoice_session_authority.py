#!/usr/bin/env python3
"""OGLVoice server-only credential and session-authority integration guard."""
from pathlib import Path
base = Path(__file__).resolve().parents[2]
issuer = (base/"NexVerse/Core/Voice/OglVoiceLiveKitTokenIssuer.cs").read_text()
proof = (base/"NexVerse/Core/Voice/OglVoiceSessionProof.cs").read_text()
endpoint = (base/"NexVerse/Server/Api/OglVoiceSessionAuthorityEndpoint.cs").read_text()
connector = (base/"NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
for value in ("HMACSHA256.HashData", "roomJoin = true",
              "canPublish = true", "canSubscribe = true",
              "TenantId != m_TenantId", "lifetimeSeconds > 300",
              "OglVoiceHypergridIdentity.DeriveGuestId", "jti = Guid.NewGuid()"):
    assert value in issuer, value
for value in ("bodyHash", "SHA256.HashData", "CryptographicOperations.FixedTimeEquals",
              "OglVoiceDiscoveryProof.ValidateKey"):
    assert value in proof, value
for value in ("AllowedNodes", "m_AllowedNodes", "m_Seen",
              "replayed_challenge", "Cache-Control", "no-store",
              "OglVoiceSessionProof.Verify", "m_Issuer.Issue",
              "MaxChallenges", "payload_too_large"):
    assert value in endpoint, value
for value in ('EnableSessionAuthority', "OGLVOICE_LIVEKIT_API_KEY",
              "OGLVOICE_LIVEKIT_API_SECRET",
              "OglVoiceSessionAuthorityEndpoint.Route"):
    assert value in connector, value
assert "EnableSessionAuthority" in (base/"bin/Robust.HG.ini.example").read_text()
assert "EnableSessionAuthority" in (base/"bin/Robust.ini.example").read_text()
assert "Environment.GetEnvironmentVariable" in connector
assert "OGLVOICE_LIVEKIT_API_SECRET" not in (base/"bin/OpenSim.ini.example").read_text()
print("OGLVoice server-only LiveKit credential and session-authority integration: OK")
