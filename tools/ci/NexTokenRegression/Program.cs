using System;
using NexVerse.Core.Security;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        const string issuer = "https://world.stadt-nexverse.de";
        const string audience = "nexverse-world-api";
        const string subject = "11111111-2222-3333-4444-555555555555";
        const int stamp = 4242;

        string key = new string('K', 64);
        HmacNexAccessTokenService tokens =
            new HmacNexAccessTokenService(issuer, audience, key, 600);

        string token = tokens.Issue(
            subject,
            new[] { NexScopes.UsersRead, NexScopes.UsersWrite, NexScopes.AdminAll },
            stamp);

        Require(token.Split('.').Length == 3, "native token must have three JWT segments");
        Require(tokens.TryValidate(token, out NexAccessTokenClaims claims), "issued token must validate");
        Require(claims.Subject == subject, "subject mismatch");
        Require(claims.SecurityStamp == stamp, "security stamp mismatch");
        Require(claims.ExpiresAt > claims.IssuedAt, "expiry must be after issue time");
        Require(claims.Scopes.Count == 3, "scope count mismatch");

        char replacement = token[token.Length - 1] == 'A' ? 'B' : 'A';
        string tampered = token.Substring(0, token.Length - 1) + replacement;
        Require(!tokens.TryValidate(tampered, out _), "tampered token must fail validation");

        HmacNexAccessTokenService wrongKey =
            new HmacNexAccessTokenService(issuer, audience, new string('Z', 64), 600);
        Require(!wrongKey.TryValidate(token, out _), "token must fail with a different signing key");

        HmacNexAccessTokenService wrongAudience =
            new HmacNexAccessTokenService(issuer, "different-audience", key, 600);
        Require(!wrongAudience.TryValidate(token, out _), "token must fail with a different audience");

        Console.WriteLine("NexVerse native access-token regression: OK");
        return 0;
    }
}
