using System;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Voice;

static void Check(bool ok, string reason)
{
    if (!ok) throw new Exception("OGLVoice Firestorm regression: " + reason);
}
static void Rejected(Action action, string reason)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Should reject " + reason);
}

string offer = "v=0\r\no=- 123 1 IN IP4 127.0.0.1\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=ice-ufrag:abc\r\n";
OglVoiceFirestormWire.ValidateOffer(offer);
Rejected(() => OglVoiceFirestormWire.ValidateOffer("video only"), "wrong SDP");
Rejected(() => OglVoiceFirestormWire.ValidateOffer("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96"), "missing audio");
Rejected(() => OglVoiceFirestormWire.ValidateOffer("v=0\r\nm=audio " +
    new string('X', OglVoiceFirestormWire.MaximumSdpBytes)), "oversized SDP");
OglVoiceFirestormWire.ValidateCandidates(new[] {
    new OglVoiceIceCandidate {
        candidate="candidate:1 1 UDP 1 127.0.0.1 1234 typ host",
        sdpMid="audio", sdpMLineIndex=0
    }
}, false);
OglVoiceFirestormWire.ValidateCandidates(Array.Empty<OglVoiceIceCandidate>(), true);
Rejected(() => OglVoiceFirestormWire.ValidateCandidates(Array.Empty<OglVoiceIceCandidate>(), false),
    "empty ICE without completed");
Rejected(() => OglVoiceFirestormWire.ValidateCandidates(new[] {
    new OglVoiceIceCandidate { candidate="bad", sdpMLineIndex=-1 }
}, false), "invalid mline");
Rejected(() => OglVoiceFirestormWire.ValidateCandidates(new[] {
    new OglVoiceIceCandidate { candidate=new string('X', 2048), sdpMLineIndex=0 }
}, false), "oversized candidate");

byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new OglVoiceMediaReply {
    protocol=OglVoiceMediaExchange.Protocol,
    viewer_session="webRtcSession-123",
    sdp_answer=offer
});
OglVoiceMediaReply answer = OglVoiceFirestormWire.ParseAnswer(encoded, true);
Check(answer.sdp_answer == offer && answer.viewer_session == "webRtcSession-123",
    "JSEP answer envelope");
Rejected(() => OglVoiceFirestormWire.ParseAnswer(
    Encoding.UTF8.GetBytes("{\"protocol\":\"rogue\",\"viewer_session\":\"one\",\"sdp_answer\":\"v=0\\r\\nm=audio 9\"}"), true),
    "wrong protocol");
Rejected(() => OglVoiceFirestormWire.ParseAnswer(
    Encoding.UTF8.GetBytes("{\"protocol\":\"oglvoice-firestorm-media-v1\",\"viewer_session\":\"bad/token\",\"sdp_answer\":\"v=0\\r\\nm=audio 9\"}"), true),
    "untrusted viewer session");
Rejected(() => OglVoiceFirestormWire.ParseAnswer(new byte[OglVoiceFirestormWire.MaximumResponseBytes+1], true),
    "large backend response");
Guid alice=Guid.NewGuid(), bob=Guid.NewGuid();
using JsonDocument join = JsonDocument.Parse(OglVoiceFirestormWire.Join(alice));
Check(join.RootElement.GetProperty(alice.ToString("D")).GetProperty("j").GetProperty("p").GetBoolean(),
    "joined avatar remote orb");
using JsonDocument speak=JsonDocument.Parse(OglVoiceFirestormWire.Speaking(bob, 0.5f, true));
JsonElement bobObj=speak.RootElement.GetProperty(bob.ToString("D"));
Check(bobObj.GetProperty("v").GetBoolean() && bobObj.GetProperty("p").GetInt32()==64,
    "remote speaking orb power");
using JsonDocument silence=JsonDocument.Parse(OglVoiceFirestormWire.Speaking(bob, 0, false));
Check(!silence.RootElement.GetProperty(bob.ToString("D")).GetProperty("v").GetBoolean(),
    "remote speaking silence");
using JsonDocument leave=JsonDocument.Parse(OglVoiceFirestormWire.Leave(alice));
Check(leave.RootElement.GetProperty(alice.ToString("D")).GetProperty("l").GetBoolean(),
    "remote orb removal");
Rejected(() => OglVoiceFirestormWire.Speaking(Guid.Empty, .5f, true), "empty avatar UUID");
Rejected(() => OglVoiceFirestormWire.Speaking(bob, float.NaN, true), "nonfinite level");

OglVoiceProviderDescriptor safe = new() {
    provider_url="https://voice.example.test",
    media_gateway_url="https://bridge.example.test/internal/voice/v1",
    tenant_id="nexverse", viewer_capability="firestorm-webrtc-v1"
};
safe.Validate();
Rejected(() => new OglVoiceProviderDescriptor {
    provider_url="https://voice.example.test",
    tenant_id="nexverse", viewer_capability="firestorm-webrtc-v1"
}.Validate(), "voice enabled without actual media gateway URL");
Rejected(() => new OglVoiceProviderDescriptor {
    provider_url="https://voice.example.test",
    media_gateway_url="http://public.example.org/gateway",
    tenant_id="nexverse", viewer_capability="firestorm-webrtc-v1"
}.Validate(), "insecure bridge");
const string sharedSecret = "ci-test-media-signed-key-over-32bytes";
string challenge = OglVoiceDiscoveryProof.NewNonce();
long now = 1791568800;
byte[] largeSdpRequest = Encoding.UTF8.GetBytes(new string('a', 8192));
string signedMedia = OglVoiceMediaProof.Sign(sharedSecret, "sim-freiburg", now, challenge, largeSdpRequest);
Check(OglVoiceMediaProof.Verify(sharedSecret, "sim-freiburg", now.ToString(), challenge,
    largeSdpRequest, signedMedia, DateTimeOffset.FromUnixTimeSeconds(now)), "large SDP envelope HMAC");
largeSdpRequest[10] ^= 1;
Check(!OglVoiceMediaProof.Verify(sharedSecret, "sim-freiburg", now.ToString(), challenge,
    largeSdpRequest, signedMedia, DateTimeOffset.FromUnixTimeSeconds(now)), "media integrity");
Check(!OglVoiceMediaProof.Verify(sharedSecret, "sim-stuttgart", now.ToString(), challenge,
    Encoding.UTF8.GetBytes(new string('a', 8192)), signedMedia,
    DateTimeOffset.FromUnixTimeSeconds(now)), "simulator node HMAC binding");
Check(!OglVoiceMediaProof.Verify(sharedSecret, "sim-freiburg", now.ToString(), challenge,
    Encoding.UTF8.GetBytes(new string('a', 8192)), signedMedia,
    DateTimeOffset.FromUnixTimeSeconds(now + 70)), "media signature expires");
Rejected(() => OglVoiceMediaProof.Sign(sharedSecret, "sim-freiburg", now, challenge,
    new byte[OglVoiceMediaProof.MaximumPayloadBytes + 1]), "unbounded media request");
Console.WriteLine("Firestorm SDP/ICE, CAPS answer, speaker data channel and secure grid provider gating: OK");
