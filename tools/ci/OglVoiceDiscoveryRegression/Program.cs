using System;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Voice;

static void Expect(bool result, string why)
{
    if (!result) throw new InvalidOperationException("OGLVoice discovery regression: " + why);
}
static void MustReject(Action action, string why)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new InvalidOperationException("OGLVoice discovery must reject " + why);
}

const string key = "development-ci-32-characters-strong-key-ONLY";
const string node = "simulator-stuttgart-01";
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
string challenge = OglVoiceDiscoveryProof.NewNonce();
string proof = OglVoiceDiscoveryProof.RequestSignature(key, node, now, challenge);
Expect(OglVoiceDiscoveryProof.VerifyRequest(key, node, now.ToString(), challenge,
    proof, DateTimeOffset.FromUnixTimeSeconds(now)), "valid signed request");
Expect(!OglVoiceDiscoveryProof.VerifyRequest(key, node, now.ToString(), challenge,
    proof + "00", DateTimeOffset.FromUnixTimeSeconds(now)), "signature length");
Expect(!OglVoiceDiscoveryProof.VerifyRequest(key, "intruder", now.ToString(), challenge,
    proof, DateTimeOffset.FromUnixTimeSeconds(now)), "node binding");
Expect(!OglVoiceDiscoveryProof.VerifyRequest(key, node, (now - 61).ToString(), challenge,
    OglVoiceDiscoveryProof.RequestSignature(key, node, now - 61, challenge),
    DateTimeOffset.FromUnixTimeSeconds(now)), "timestamp window");
Expect(!OglVoiceDiscoveryProof.VerifyRequest(key, node, "bogus", challenge,
    proof, DateTimeOffset.FromUnixTimeSeconds(now)), "invalid timestamp");
MustReject(() => OglVoiceDiscoveryProof.RequestSignature(key, "bad/node", now, challenge),
    "invalid NodeId");
MustReject(() => OglVoiceDiscoveryProof.RequestSignature("short", node, now, challenge),
    "short secret");
MustReject(() => OglVoiceDiscoveryProof.RequestSignature(key, node, now, "abc"),
    "short nonce");

OglVoiceProviderDescriptor descriptor = new()
{
    provider_url = "https://voice.stadt-nexverse.de/",
    tenant_id = "nexverse",
    hypergrid_guests = true
};
descriptor.Validate();
byte[] body = JsonSerializer.SerializeToUtf8Bytes(descriptor);
string reply = OglVoiceDiscoveryProof.ResponseSignature(key, challenge, body);
Expect(OglVoiceDiscoveryProof.VerifyResponse(key, challenge, body, reply),
    "signed response");
Expect(!OglVoiceDiscoveryProof.VerifyResponse(key, OglVoiceDiscoveryProof.NewNonce(),
    body, reply), "response must bind request challenge");
body[^1] ^= 0x01;
Expect(!OglVoiceDiscoveryProof.VerifyResponse(key, challenge, body, reply),
    "tampered response");
Expect(!OglVoiceDiscoveryProof.IsSafeServiceUri("http://public.example.com"),
    "public non-TLS must be disabled");
Expect(OglVoiceDiscoveryProof.IsSafeServiceUri("http://127.0.0.1:19090"),
    "loopback HTTP allowed for tests");
Expect(!OglVoiceDiscoveryProof.IsSafeServiceUri("https://person:pass@host.example.com"),
    "URL credentials blocked");
Expect(!OglVoiceDiscoveryProof.IsSafeServiceUri("https://host.example.com/?token=abc"),
    "URL embedded token blocked");
MustReject(() => new OglVoiceProviderDescriptor { provider_url = "http://nonlocal.example",
    tenant_id = "nexverse" }.Validate(), "non-TLS provider");
MustReject(() => new OglVoiceProviderDescriptor { provider_url = "https://voice.example.com",
    tenant_id = "../root" }.Validate(), "tenant path traversal");

Guid guestA = new("0d9b8d31-a2a7-441a-9ccd-2f1b9df2a1a0");
Guid guestB = new("0d9b8d31-a2a7-441a-9ccd-2f1b9df2a1a1");
string a = OglVoiceHypergridIdentity.DeriveGuestId("https://other-grid.example/", guestA);
Expect(a == OglVoiceHypergridIdentity.DeriveGuestId("https://OTHER-GRID.example:443",
    guestA), "canonical HG identity");
Expect(a != OglVoiceHypergridIdentity.DeriveGuestId("https://another-grid.example", guestA),
    "same UUID on another grid must differ");
Expect(a != OglVoiceHypergridIdentity.DeriveGuestId("https://other-grid.example", guestB),
    "different avatar must differ");
MustReject(() => OglVoiceHypergridIdentity.DeriveGuestId("https://user:pass@grid.test",
    guestA), "HG URL credentials");
MustReject(() => OglVoiceHypergridIdentity.DeriveGuestId("https://grid.test/users/123",
    guestA), "non-grid HG home path");
MustReject(() => OglVoiceHypergridIdentity.DeriveGuestId("https://grid.test",
    Guid.Empty), "empty avatar UUID");
Console.WriteLine("OGLVoice signed discovery and Hypergrid identity runtime regression: OK");
