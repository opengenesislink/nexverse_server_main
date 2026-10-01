// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Security
{
    public sealed class NexAccessTokenClaims
    {
        public string Subject { get; }
        public IReadOnlyList<string> Scopes { get; }
        public int SecurityStamp { get; }
        public long IssuedAt { get; }
        public long ExpiresAt { get; }
        public string TokenId { get; }

        public NexAccessTokenClaims(
            string subject,
            IEnumerable<string> scopes,
            int securityStamp,
            long issuedAt,
            long expiresAt,
            string tokenId)
        {
            Subject = subject ?? string.Empty;
            Scopes = (scopes ?? Array.Empty<string>()).ToArray();
            SecurityStamp = securityStamp;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
            TokenId = tokenId ?? string.Empty;
        }
    }

    public interface INexAccessTokenService
    {
        int LifetimeSeconds { get; }
        string Issuer { get; }
        string Issue(string subject, IEnumerable<string> scopes, int securityStamp);
        string IssueIdentityToken(string subject, string clientId, string nonce, int securityStamp);
        bool TryValidate(string token, out NexAccessTokenClaims claims);
    }

    public sealed class HmacNexAccessTokenService : INexAccessTokenService
    {
        private readonly byte[] m_Key;
        private readonly string m_Issuer;
        private readonly string m_Audience;

        public int LifetimeSeconds { get; }
        public string Issuer => m_Issuer;

        public HmacNexAccessTokenService(
            string issuer,
            string audience,
            string signingKey,
            int lifetimeSeconds)
        {
            if (string.IsNullOrWhiteSpace(issuer))
                throw new ArgumentException("Token issuer is required.", nameof(issuer));
            if (string.IsNullOrWhiteSpace(audience))
                throw new ArgumentException("Token audience is required.", nameof(audience));
            if (string.IsNullOrEmpty(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
                throw new ArgumentException("Token signing key must contain at least 32 UTF-8 bytes.", nameof(signingKey));

            m_Issuer = issuer.TrimEnd('/');
            m_Audience = audience;
            m_Key = Encoding.UTF8.GetBytes(signingKey);
            LifetimeSeconds = Math.Max(60, lifetimeSeconds);
        }

        public string Issue(string subject, IEnumerable<string> scopes, int securityStamp)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Token subject is required.", nameof(subject));

            long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expiresAt = issuedAt + LifetimeSeconds;
            string tokenId = Guid.NewGuid().ToString("N");

            string[] normalizedScopes = (scopes ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            byte[] header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "HS256",
                typ = "JWT"
            });

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = m_Issuer,
                aud = m_Audience,
                sub = subject,
                iat = issuedAt,
                exp = expiresAt,
                jti = tokenId,
                scope = string.Join(" ", normalizedScopes),
                nxs = securityStamp
            });

            string unsignedToken = Base64UrlEncode(header) + "." + Base64UrlEncode(payload);
            byte[] signature = Sign(unsignedToken);
            return unsignedToken + "." + Base64UrlEncode(signature);
        }

        public string IssueIdentityToken(
            string subject,
            string clientId,
            string nonce,
            int securityStamp)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Token subject is required.", nameof(subject));
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("OIDC client ID is required.", nameof(clientId));

            long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expiresAt = issuedAt + LifetimeSeconds;

            byte[] header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "HS256",
                typ = "JWT"
            });

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = m_Issuer,
                aud = clientId,
                sub = subject,
                iat = issuedAt,
                exp = expiresAt,
                jti = Guid.NewGuid().ToString("N"),
                nonce = nonce ?? string.Empty,
                nxs = securityStamp
            });

            string unsignedToken = Base64UrlEncode(header) + "." + Base64UrlEncode(payload);
            return unsignedToken + "." + Base64UrlEncode(Sign(unsignedToken));
        }

        public bool TryValidate(string token, out NexAccessTokenClaims claims)
        {
            claims = null;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            string[] parts = token.Split('.');
            if (parts.Length != 3)
                return false;

            string unsignedToken = parts[0] + "." + parts[1];

            byte[] suppliedSignature;
            try
            {
                suppliedSignature = Base64UrlDecode(parts[2]);

                // Require canonical base64url segments.  Some decoders accept
                // alternative trailing pad bits that decode to the same bytes,
                // which makes an otherwise signed token string malleable.
                if (!string.Equals(
                    Base64UrlEncode(Base64UrlDecode(parts[0])),
                    parts[0],
                    StringComparison.Ordinal) ||
                    !string.Equals(
                    Base64UrlEncode(Base64UrlDecode(parts[1])),
                    parts[1],
                    StringComparison.Ordinal) ||
                    !string.Equals(
                    Base64UrlEncode(suppliedSignature),
                    parts[2],
                    StringComparison.Ordinal))
                    return false;
            }
            catch
            {
                return false;
            }

            byte[] expectedSignature = Sign(unsignedToken);
            if (suppliedSignature.Length != expectedSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
                return false;

            try
            {
                using JsonDocument header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
                if (!header.RootElement.TryGetProperty("alg", out JsonElement alg) ||
                    !string.Equals(alg.GetString(), "HS256", StringComparison.Ordinal))
                    return false;

                using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
                JsonElement root = payload.RootElement;

                if (!TryGetString(root, "iss", out string issuer) ||
                    !TryGetString(root, "aud", out string audience) ||
                    !TryGetString(root, "sub", out string subject) ||
                    !TryGetString(root, "jti", out string tokenId) ||
                    !root.TryGetProperty("iat", out JsonElement iatElement) ||
                    !iatElement.TryGetInt64(out long issuedAt) ||
                    !root.TryGetProperty("exp", out JsonElement expElement) ||
                    !expElement.TryGetInt64(out long expiresAt) ||
                    !root.TryGetProperty("nxs", out JsonElement stampElement) ||
                    !stampElement.TryGetInt32(out int securityStamp))
                    return false;

                if (!string.Equals(issuer.TrimEnd('/'), m_Issuer, StringComparison.Ordinal) ||
                    !string.Equals(audience, m_Audience, StringComparison.Ordinal))
                    return false;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (issuedAt > now + 60 || expiresAt <= now || expiresAt <= issuedAt)
                    return false;

                string scope = string.Empty;
                if (root.TryGetProperty("scope", out JsonElement scopeElement) &&
                    scopeElement.ValueKind == JsonValueKind.String)
                    scope = scopeElement.GetString() ?? string.Empty;

                string[] scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                claims = new NexAccessTokenClaims(subject, scopes, securityStamp, issuedAt, expiresAt, tokenId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private byte[] Sign(string value)
        {
            using HMACSHA256 hmac = new HMACSHA256(m_Key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        }

        private static bool TryGetString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
                return false;

            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
                case 1:
                    throw new FormatException("Invalid base64url value.");
            }

            return Convert.FromBase64String(padded);
        }
    }
}
