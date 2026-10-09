#!/usr/bin/env python3
"""OGLVoice provider-discovery startup/security integration regression."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
core = (root/"NexVerse/Core/Voice/OglVoiceDiscoveryProof.cs").read_text()
endpoint = (root/"NexVerse/Server/Api/OglVoiceGridDiscoveryEndpoint.cs").read_text()
connector = (root/"NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
module = (root/"NexVerse/RegionModules/Voice/OglVoiceRegionDiscoveryModule.cs").read_text()
identity = (root/"NexVerse/Core/Voice/OglVoiceHypergridIdentity.cs").read_text()
for marker in ("HMACSHA256.HashData", "CryptographicOperations.FixedTimeEquals",
               "nonce", "VerifyResponse", "IsSafeServiceUri", "provider_url",
               "media_transport", "viewer_capability"):
    assert marker in core, marker
for marker in ("VerifyRequest", "m_Seen", "replayed_challenge", "MaxOutstandingChallenges",
               "ResponseSignature", "/internal/oglvoice/v1/provider",
               'request.HttpMethod, "GET"', "Cache-Control"):
    assert marker in endpoint, marker
for marker in ('Configs["OGLVoice"]', 'Configs["NexBus"]',
               "OglVoiceGridDiscoveryEndpoint", "provider.Validate()"):
    assert marker in connector, marker
for marker in ('Configs["NexVerseNodeAgent"]', "OglVoiceDiscoveryProof.RequestSignature",
               "OglVoiceDiscoveryProof.VerifyResponse", "Mode", "GridManaged",
               "Standalone", "AddRegion", "RemoveRegion", "m_Timer",
               "CurrentProvider", "TimeSpan.FromMinutes(10)"):
    assert marker in module, marker
assert "HMACSHA256" in core and "SHA256.HashData" in identity
assert "LiveKitAdminKey" not in endpoint and "LiveKitAdminKey" not in module
assert 'RequestModuleInterface<IOglVoiceProviderLookup>' not in connector
for path in ("bin/Robust.ini.example", "bin/Robust.HG.ini.example"):
    config = (root/path).read_text()
    assert "[OGLVoice]" in config and "Enabled = false" in config
    assert 'Mode = "GridAuthority"' in config
    assert 'IncludeHypergridGuests = true' in config
sim = (root/"bin/OpenSim.ini.example").read_text()
assert ';[OGLVoice]' in sim and 'Mode = "Standalone"' in sim
roadmap = (root/"doc/NexVerse/OGLVOICE_ARCHITECTURE.md").read_text()
assert "nicht" in roadmap.lower() and "LiveKit" in roadmap
print("OGLVoice Robust/Standalone signed provider discovery CI guard: OK")
