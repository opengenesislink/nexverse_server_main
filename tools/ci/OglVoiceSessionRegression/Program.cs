using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Voice;

static void Assert(bool b, string msg) { if (!b) throw new Exception("OGLVoice session regression: " + msg); }
static void Reject(Action a, string msg)
{
    try { a(); } catch (Exception e) when (
        e is ArgumentException || e is UnauthorizedAccessException) { return; }
    throw new Exception("Expected rejection: " + msg);
}
const string key = "oglvoice-test-key", secret = "ci-only-never-production-use-0123456789-abcdef";
OglVoiceLiveKitTokenIssuer issuer = new("nexverse", key, secret);
Guid region = Guid.NewGuid(), avatar = Guid.NewGuid(), session = Guid.NewGuid();
OglVoiceAdmission local = new()
{
    TenantId = "nexverse", RegionId = region,
    AvatarId = avatar, SessionId = session, VoiceAllowed = true
};
DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1791568800);
OglVoiceIssuedToken token = issuer.Issue(local, now);
string[] parts = token.Token.Split('.');
Assert(parts.Length == 3, "compact JWT");
static byte[] Decode(string data)
{
    string p = data.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(p.PadRight((p.Length + 3) / 4 * 4, '='));
}
using JsonDocument header = JsonDocument.Parse(Decode(parts[0]));
using JsonDocument body = JsonDocument.Parse(Decode(parts[1]));
Assert(header.RootElement.GetProperty("alg").GetString() == "HS256", "algorithm");
byte[] expectedSig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
    Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
Assert(CryptographicOperations.FixedTimeEquals(expectedSig, Decode(parts[2])), "signature");
JsonElement claims = body.RootElement;
Assert(claims.GetProperty("iss").GetString() == key, "issuer");
Assert(claims.GetProperty("sub").GetString() == token.ParticipantIdentity, "identity");
Assert(claims.GetProperty("iat").GetInt64() == now.ToUnixTimeSeconds(), "issued at");
Assert(claims.GetProperty("exp").GetInt64() == now.ToUnixTimeSeconds() + 120, "expiry");
JsonElement grant = claims.GetProperty("video");
Assert(grant.GetProperty("roomJoin").GetBoolean(), "room join grant");
Assert(grant.GetProperty("room").GetString() == "ogl.nexverse.region." + region.ToString("N"),
    "isolated region room");
Assert(grant.GetProperty("canPublish").GetBoolean() && grant.GetProperty("canSubscribe").GetBoolean(), "audio grant");
Assert(!grant.TryGetProperty("roomAdmin", out _), "no admin permissions");
Assert(!grant.TryGetProperty("roomList", out _), "no global room list");
Assert(!grant.TryGetProperty("roomCreate", out _), "no room create");
Assert(token.Token.Length > 200, "signed token present");
Assert(!token.Token.Contains(secret), "no inline secret");

Reject(() => issuer.Issue(new OglVoiceAdmission
    { TenantId = "external", RegionId = region, AvatarId = avatar,
      SessionId = session, VoiceAllowed = true }, now), "cross-tenant");
Reject(() => issuer.Issue(new OglVoiceAdmission
    { TenantId = "nexverse", RegionId = region, AvatarId = avatar,
      SessionId = session, VoiceAllowed = false }, now), "voice disallowed");
Reject(() => issuer.Issue(new OglVoiceAdmission
    { TenantId = "nexverse", RegionId = region, AvatarId = avatar,
      SessionId = Guid.Empty, VoiceAllowed = true }, now), "missing session");
Reject(() => issuer.Issue(new OglVoiceAdmission
    { TenantId = "nexverse", RegionId = region, AvatarId = avatar,
      SessionId = session, VoiceAllowed = true, IsHypergridGuest = true }, now), "guest without home");
Reject(() => issuer.Issue(new OglVoiceAdmission
    { TenantId = "nexverse", RegionId = region, AvatarId = avatar,
      SessionId = session, VoiceAllowed = true, HomeGridOrigin = "https://forged.example" },
    now), "local home injection");
Reject(() => issuer.Issue(local, now, 600), "excessive TTL");
Reject(() => new OglVoiceLiveKitTokenIssuer("../nexverse", key, secret), "invalid tenant");

OglVoiceIssuedToken guest = issuer.Issue(new OglVoiceAdmission
{
    TenantId = "nexverse", RegionId = region, AvatarId = avatar, SessionId = session,
    IsHypergridGuest = true, HomeGridOrigin = "https://osgrid.example", VoiceAllowed = true
}, now);
Assert(guest.Room == token.Room, "guest participates in visiting grid");
OglVoiceIssuedToken httpHgGuest = issuer.Issue(new OglVoiceAdmission
{
    TenantId = "nexverse", RegionId = region, AvatarId = avatar, SessionId = session,
    IsHypergridGuest = true, HomeGridOrigin = "http://legacy-hg.example:8002",
    VoiceAllowed = true
}, now);
Assert(httpHgGuest.Room == token.Room, "legacy HTTP Hypergrid identity does not disable voice");
Assert(guest.ParticipantIdentity != token.ParticipantIdentity, "HG identity isolation");
Assert(guest.ParticipantIdentity != issuer.Issue(new OglVoiceAdmission {
    TenantId = "nexverse", RegionId = region, AvatarId = avatar, SessionId = session,
    IsHypergridGuest = true, HomeGridOrigin = "https://different-grid.example",
    VoiceAllowed = true }, now).ParticipantIdentity, "HG origin isolation");
Assert(token.ParticipantIdentity != issuer.Issue(new OglVoiceAdmission {
    TenantId = "nexverse", RegionId = region, AvatarId = avatar, SessionId = Guid.NewGuid(),
    VoiceAllowed = true }, now).ParticipantIdentity, "reconnect session isolation");

const string nodeKey = "ci-nexbus-32byte-shared-key-testing-ONLY";
string nonce = OglVoiceDiscoveryProof.NewNonce();
byte[] signedBody = JsonSerializer.SerializeToUtf8Bytes(local);
long stamp = now.ToUnixTimeSeconds();
string proof = OglVoiceSessionProof.Sign(nodeKey, "simulator-a", stamp, nonce, signedBody);
Assert(OglVoiceSessionProof.Verify(nodeKey, "simulator-a", stamp.ToString(), nonce,
    signedBody, proof, now), "valid body-bound HMAC");
byte[] changed = (byte[])signedBody.Clone(); changed[^2] ^= 0x01;
Assert(!OglVoiceSessionProof.Verify(nodeKey, "simulator-a", stamp.ToString(), nonce,
    changed, proof, now), "tampered body");
Assert(!OglVoiceSessionProof.Verify(nodeKey, "simulator-b", stamp.ToString(), nonce,
    signedBody, proof, now), "wrong node");
Assert(!OglVoiceSessionProof.Verify(nodeKey, "simulator-a", stamp.ToString(), nonce,
    signedBody, proof, now.AddMinutes(2)), "expired request");
Console.WriteLine("OGLVoice LiveKit JWT and authenticated session assertions: OK");
