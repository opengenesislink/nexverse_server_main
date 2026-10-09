#!/usr/bin/env python3
"""Contract guard: Firestorm WebRTC viewer capabilities are opt-in and secure."""
from pathlib import Path

root=Path(__file__).resolve().parents[2]
region=(root/"NexVerse/RegionModules/Voice/OglVoiceFirestormCapsModule.cs").read_text()
wire=(root/"NexVerse/Core/Voice/OglVoiceFirestormWire.cs").read_text()
provider=(root/"NexVerse/Core/Voice/OglVoiceDiscoveryProof.cs").read_text()
robust=(root/"NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
node=(root/"NexVerse/RegionModules/Voice/OglVoiceRegionDiscoveryModule.cs").read_text()
for expected in ("OnRegisterCaps += RegisterCaps",
                 "OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest",
                 "VoiceServerType", 'OSD.FromString("webrtc")',
                 '"ProvisionVoiceAccountRequest"', '"VoiceSignalingRequest"',
                 "OSDParser.DeserializeLLSDXml",
                 "OSDParser.SerializeLLSDXmlString",
                 'offerType.AsString() != "offer"',
                 "OglVoiceFirestormWire.ValidateOffer",
                 "OglVoiceFirestormWire.ValidateCandidates",
                 "OglVoiceSessionProof.Sign",
                 "IOglVoiceSessionAdmission",
                 "TryBuildAdmission",
                 "m_Sessions", "m_Requests.Wait(0)",
                 "OglVoiceMediaExchange", "HttpClientHandler"):
    assert expected in region, expected
for expected in ("media_gateway_url", "firestorm-webrtc-v1", "IsSafeServiceUri"):
    assert expected in provider, expected
assert "EnableFirestormGateway" in robust
assert "EnableFirestormGateway" in node
assert "Firestorm" in wire
for word in ('["jsep"]','["viewer_session"]', 'viewer_session', 'candidate'):
    assert word in region
for word in ("p = Math.Clamp", "v = isSpeaking", "l = true", "j = new", "MaximumSdpBytes"):
    assert word in wire, word
for conf in ("bin/Robust.HG.ini.example","bin/Robust.ini.example"):
    section=(root/conf).read_text().split("[OGLVoice]",1)[1].split("\n[",1)[0]
    assert "EnableFirestormGateway = false" in section
    assert 'MediaGatewayUrl = ""' in section
assert "EnableFirestormGateway = true" in (root/"bin/OpenSim.ini.example").read_text()
assert "OGLVOICE_LIVEKIT_API_SECRET" not in region
assert "OGLVOICE_LIVEKIT_API_KEY" not in region
assert "ChatSessionRequest" not in region  # no false group voice support
print("OGLVoice Firestorm LLSD CAPS and speaking-orb security guard: OK")
