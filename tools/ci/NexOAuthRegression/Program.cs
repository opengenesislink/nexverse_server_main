using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Security;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        if (padded.Length % 4 == 2) padded += "==";
        else if (padded.Length % 4 == 3) padded += "=";
        return Convert.FromBase64String(padded);
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexverse-oauth-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string storePath = Path.Combine(root, "auth.json");
            PersistentNexOAuthStore store = new PersistentNexOAuthStore(storePath);

            NexOAuthClientRegistration web = store.CreateClient(
                "Regression Web",
                NexOAuthClientTypes.Confidential,
                new[] { "https://client.example/callback" },
                new[] { NexScopes.OpenId, NexScopes.Profile, NexScopes.OfflineAccess, NexScopes.UsersRead });

            Require(!string.IsNullOrWhiteSpace(web.ClientSecret), "confidential client secret missing");
            Require(store.ValidateClientSecret(web.Client.ClientId, web.ClientSecret), "client secret validation failed");
            Require(!store.ValidateClientSecret(web.Client.ClientId, web.ClientSecret + "x"), "wrong client secret accepted");

            // Persistence across process/store instances.
            PersistentNexOAuthStore reloaded = new PersistentNexOAuthStore(storePath);
            Require(reloaded.GetClient(web.Client.ClientId) != null, "client did not persist");
            Require(reloaded.ValidateClientSecret(web.Client.ClientId, web.ClientSecret), "persisted client secret validation failed");

            string verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string code = reloaded.CreateAuthorizationCode(
                web.Client.ClientId,
                "11111111-2222-3333-4444-555555555555",
                "https://client.example/callback",
                new[] { NexScopes.OpenId, NexScopes.OfflineAccess, NexScopes.UsersRead },
                challenge,
                "nonce-1",
                77,
                120);

            Require(!reloaded.TryConsumeAuthorizationCode(
                code,
                web.Client.ClientId,
                "https://client.example/callback",
                verifier + "wrong",
                out _), "wrong PKCE verifier accepted");

            Require(reloaded.TryConsumeAuthorizationCode(
                code,
                web.Client.ClientId,
                "https://client.example/callback",
                verifier,
                out NexAuthorizationGrant grant), "valid PKCE code rejected");
            Require(grant.SecurityStamp == 77, "authorization grant security stamp mismatch");
            Require(!reloaded.TryConsumeAuthorizationCode(
                code,
                web.Client.ClientId,
                "https://client.example/callback",
                verifier,
                out _), "authorization code was reusable");

            NexRefreshTokenIssue refresh = reloaded.CreateRefreshToken(
                web.Client.ClientId,
                grant.Subject,
                grant.Scopes,
                grant.SecurityStamp,
                3600);

            Require(reloaded.TryRotateRefreshToken(
                refresh.Token,
                web.Client.ClientId,
                3600,
                out NexRefreshGrant previous,
                out NexRefreshTokenIssue replacement), "refresh rotation failed");
            Require(previous.Revoked, "rotated token was not revoked");
            Require(!reloaded.TryRotateRefreshToken(
                refresh.Token,
                web.Client.ClientId,
                3600,
                out _,
                out _), "old refresh token remained usable");
            Require(reloaded.RevokeRefreshToken(replacement.Token), "replacement refresh revoke failed");

            NexOAuthClientRegistration service = reloaded.CreateClient(
                "Regression Service",
                NexOAuthClientTypes.Service,
                Array.Empty<string>(),
                new[] { NexScopes.UsersRead, NexScopes.RegionsRead });

            Require(reloaded.ValidateClientSecret(service.Client.ClientId, service.ClientSecret), "service secret invalid");
            string serviceSubject = "service:" + service.Client.ClientId;
            Require(reloaded.ValidateServicePrincipal(
                serviceSubject,
                service.Client.SecurityStamp,
                new[] { NexScopes.UsersRead }), "service principal validation failed");
            Require(!reloaded.ValidateServicePrincipal(
                serviceSubject,
                service.Client.SecurityStamp,
                new[] { NexScopes.AdminAll }), "service principal accepted unauthorized scope");

            Require(reloaded.SetClientEnabled(service.Client.ClientId, false), "service disable failed");
            Require(!reloaded.ValidateServicePrincipal(
                serviceSubject,
                service.Client.SecurityStamp,
                new[] { NexScopes.UsersRead }), "disabled service principal remained valid");

            reloaded.RevokeAccessToken("jti-regression", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600);
            Require(reloaded.IsAccessTokenRevoked("jti-regression"), "access token revocation did not persist");

            string keyPath = Path.Combine(root, "oidc.pem");
            string issuer = "https://world.example";
            string idToken;
            string kid;

            using (PersistentEs256OidcSigningService signer =
                new PersistentEs256OidcSigningService(issuer, keyPath, 600))
            {
                kid = signer.KeyId;
                idToken = signer.IssueIdentityToken(
                    grant.Subject,
                    web.Client.ClientId,
                    "nonce-1",
                    grant.SecurityStamp);

                string[] parts = idToken.Split('.');
                Require(parts.Length == 3, "OIDC ID token shape invalid");

                using JsonDocument header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
                Require(header.RootElement.GetProperty("alg").GetString() == "ES256", "ID token algorithm is not ES256");
                Require(header.RootElement.GetProperty("kid").GetString() == kid, "ID token kid mismatch");

                using JsonDocument jwks = JsonDocument.Parse(signer.GetJwksJson());
                JsonElement key = jwks.RootElement.GetProperty("keys")[0];
                Require(key.GetProperty("kid").GetString() == kid, "JWKS kid mismatch");

                ECParameters parameters = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = Base64UrlDecode(key.GetProperty("x").GetString()),
                        Y = Base64UrlDecode(key.GetProperty("y").GetString())
                    }
                };

                using ECDsa publicKey = ECDsa.Create(parameters);
                bool signatureOk = publicKey.VerifyData(
                    Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                    Base64UrlDecode(parts[2]),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                Require(signatureOk, "ES256 ID token signature verification failed");
            }

            using (PersistentEs256OidcSigningService signer =
                new PersistentEs256OidcSigningService(issuer, keyPath, 600))
            {
                Require(signer.KeyId == kid, "OIDC signing key was not persistent");
            }

            Console.WriteLine("NexVerse OAuth2/OIDC regression: OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
