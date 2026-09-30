// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Security
{
    public static class NexOAuthClientTypes
    {
        public const string Public = "public";
        public const string Confidential = "confidential";
        public const string Service = "service";

        public static bool IsSupported(string value)
        {
            return value == Public || value == Confidential || value == Service;
        }
    }

    public sealed class NexOAuthClient
    {
        public string ClientId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ClientType { get; set; } = NexOAuthClientTypes.Public;
        public string[] RedirectUris { get; set; } = Array.Empty<string>();
        public string[] AllowedScopes { get; set; } = Array.Empty<string>();
        public string SecretSalt { get; set; } = string.Empty;
        public string SecretHash { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int SecurityStamp { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public bool RequiresSecret =>
            ClientType == NexOAuthClientTypes.Confidential ||
            ClientType == NexOAuthClientTypes.Service;
    }

    public sealed class NexOAuthClientRegistration
    {
        public NexOAuthClient Client { get; }
        public string ClientSecret { get; }

        public NexOAuthClientRegistration(NexOAuthClient client, string clientSecret)
        {
            Client = client;
            ClientSecret = clientSecret ?? string.Empty;
        }
    }

    public sealed class NexAuthorizationGrant
    {
        public string ClientId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = string.Empty;
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public string CodeChallenge { get; set; } = string.Empty;
        public string CodeChallengeMethod { get; set; } = "S256";
        public string Nonce { get; set; } = string.Empty;
        public int SecurityStamp { get; set; }
        public long ExpiresAt { get; set; }
    }

    public sealed class NexRefreshGrant
    {
        public string ClientId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public int SecurityStamp { get; set; }
        public long CreatedAt { get; set; }
        public long ExpiresAt { get; set; }
        public bool Revoked { get; set; }
    }

    public sealed class NexRefreshTokenIssue
    {
        public string Token { get; }
        public NexRefreshGrant Grant { get; }

        public NexRefreshTokenIssue(string token, NexRefreshGrant grant)
        {
            Token = token ?? string.Empty;
            Grant = grant;
        }
    }

    public interface INexOAuthStore
    {
        NexOAuthClientRegistration CreateClient(
            string name,
            string clientType,
            IEnumerable<string> redirectUris,
            IEnumerable<string> allowedScopes);

        IReadOnlyList<NexOAuthClient> ListClients();
        NexOAuthClient GetClient(string clientId);
        bool ValidateClientSecret(string clientId, string clientSecret);
        bool ValidateServicePrincipal(string subject, int securityStamp, IEnumerable<string> scopes);

        string CreateAuthorizationCode(
            string clientId,
            string subject,
            string redirectUri,
            IEnumerable<string> scopes,
            string codeChallenge,
            string nonce,
            int securityStamp,
            int lifetimeSeconds);

        bool TryConsumeAuthorizationCode(
            string code,
            string clientId,
            string redirectUri,
            string codeVerifier,
            out NexAuthorizationGrant grant);

        NexRefreshTokenIssue CreateRefreshToken(
            string clientId,
            string subject,
            IEnumerable<string> scopes,
            int securityStamp,
            int lifetimeSeconds);

        bool TryRotateRefreshToken(
            string refreshToken,
            string clientId,
            int lifetimeSeconds,
            out NexRefreshGrant previousGrant,
            out NexRefreshTokenIssue replacement);

        bool RevokeRefreshToken(string refreshToken);
        int RevokeSubjectRefreshTokens(string subject);
        void RevokeAccessToken(string tokenId, long expiresAt);
        bool IsAccessTokenRevoked(string tokenId);
    }

    public sealed class PersistentNexOAuthStore : INexOAuthStore
    {
        private const int SecretIterations = 120000;
        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private AuthStoreDocument m_Document;

        public PersistentNexOAuthStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Auth store path is required.", nameof(path));

            m_Path = Path.GetFullPath(path);
            m_Document = Load();
            CleanupExpired();
        }

        public NexOAuthClientRegistration CreateClient(
            string name,
            string clientType,
            IEnumerable<string> redirectUris,
            IEnumerable<string> allowedScopes)
        {
            string normalizedType = (clientType ?? string.Empty).Trim().ToLowerInvariant();
            if (!NexOAuthClientTypes.IsSupported(normalizedType))
                throw new ArgumentException("Unsupported OAuth client type.", nameof(clientType));

            string[] redirects = NormalizeValues(redirectUris);
            string[] scopes = NormalizeValues(allowedScopes);

            if (normalizedType != NexOAuthClientTypes.Service && redirects.Length == 0)
                throw new ArgumentException("Interactive OAuth clients require at least one redirect URI.", nameof(redirectUris));

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string secret = string.Empty;
            string salt = string.Empty;
            string hash = string.Empty;

            if (normalizedType != NexOAuthClientTypes.Public)
            {
                secret = GenerateOpaqueToken(48);
                HashSecret(secret, out salt, out hash);
            }

            NexOAuthClient client = new NexOAuthClient
            {
                ClientId = "nx_" + Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "NexVerse client" : name.Trim(),
                ClientType = normalizedType,
                RedirectUris = redirects,
                AllowedScopes = scopes,
                SecretSalt = salt,
                SecretHash = hash,
                Enabled = true,
                SecurityStamp = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            lock (m_Sync)
            {
                m_Document.Clients.Add(client);
                SaveLocked();
            }

            return new NexOAuthClientRegistration(CloneClient(client), secret);
        }

        public IReadOnlyList<NexOAuthClient> ListClients()
        {
            lock (m_Sync)
            {
                return m_Document.Clients
                    .Select(CloneClient)
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        public NexOAuthClient GetClient(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return null;

            lock (m_Sync)
            {
                NexOAuthClient client = m_Document.Clients.FirstOrDefault(
                    x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));
                return client == null ? null : CloneClient(client);
            }
        }

        public bool ValidateClientSecret(string clientId, string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrEmpty(clientSecret))
                return false;

            lock (m_Sync)
            {
                NexOAuthClient client = m_Document.Clients.FirstOrDefault(
                    x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));

                return client != null &&
                       client.Enabled &&
                       client.RequiresSecret &&
                       VerifySecret(clientSecret, client.SecretSalt, client.SecretHash);
            }
        }

        public bool ValidateServicePrincipal(string subject, int securityStamp, IEnumerable<string> scopes)
        {
            const string prefix = "service:";
            if (string.IsNullOrWhiteSpace(subject) ||
                !subject.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string clientId = subject.Substring(prefix.Length);
            lock (m_Sync)
            {
                NexOAuthClient client = m_Document.Clients.FirstOrDefault(
                    x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));

                if (client == null ||
                    !client.Enabled ||
                    client.ClientType != NexOAuthClientTypes.Service ||
                    client.SecurityStamp != securityStamp)
                    return false;

                HashSet<string> allowed = new HashSet<string>(
                    client.AllowedScopes ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                return (scopes ?? Array.Empty<string>()).All(allowed.Contains);
            }
        }

        public string CreateAuthorizationCode(
            string clientId,
            string subject,
            string redirectUri,
            IEnumerable<string> scopes,
            string codeChallenge,
            string nonce,
            int securityStamp,
            int lifetimeSeconds)
        {
            if (string.IsNullOrWhiteSpace(codeChallenge) || codeChallenge.Length < 43)
                throw new ArgumentException("PKCE S256 code challenge is required.", nameof(codeChallenge));

            NexOAuthClient client = GetRequiredEnabledClient(clientId);
            if (client.ClientType == NexOAuthClientTypes.Service)
                throw new InvalidOperationException("Service clients cannot use authorization_code.");

            if (!client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                throw new InvalidOperationException("redirect_uri is not registered.");

            string[] requestedScopes = ValidateScopes(client, scopes);
            string code = GenerateOpaqueToken(48);
            string codeHash = HashOpaqueToken(code);

            NexAuthorizationGrant grant = new NexAuthorizationGrant
            {
                ClientId = client.ClientId,
                Subject = subject ?? string.Empty,
                RedirectUri = redirectUri ?? string.Empty,
                Scopes = requestedScopes,
                CodeChallenge = codeChallenge,
                CodeChallengeMethod = "S256",
                Nonce = nonce ?? string.Empty,
                SecurityStamp = securityStamp,
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(60, lifetimeSeconds)
            };

            lock (m_Sync)
            {
                m_Document.AuthorizationCodes[codeHash] = grant;
                SaveLocked();
            }

            return code;
        }

        public bool TryConsumeAuthorizationCode(
            string code,
            string clientId,
            string redirectUri,
            string codeVerifier,
            out NexAuthorizationGrant grant)
        {
            grant = null;
            if (string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(codeVerifier))
                return false;

            string codeHash = HashOpaqueToken(code);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (m_Sync)
            {
                if (!m_Document.AuthorizationCodes.TryGetValue(codeHash, out NexAuthorizationGrant stored))
                    return false;

                if (stored.ExpiresAt <= now ||
                    !string.Equals(stored.ClientId, clientId, StringComparison.Ordinal) ||
                    !string.Equals(stored.RedirectUri, redirectUri, StringComparison.Ordinal) ||
                    !VerifyPkceS256(codeVerifier, stored.CodeChallenge))
                    return false;

                m_Document.AuthorizationCodes.Remove(codeHash);
                SaveLocked();
                grant = CloneAuthorizationGrant(stored);
                return true;
            }
        }

        public NexRefreshTokenIssue CreateRefreshToken(
            string clientId,
            string subject,
            IEnumerable<string> scopes,
            int securityStamp,
            int lifetimeSeconds)
        {
            NexOAuthClient client = GetRequiredEnabledClient(clientId);
            string[] normalizedScopes = ValidateScopes(client, scopes);
            string token = GenerateOpaqueToken(64);

            NexRefreshGrant grant = new NexRefreshGrant
            {
                ClientId = clientId,
                Subject = subject ?? string.Empty,
                Scopes = normalizedScopes,
                SecurityStamp = securityStamp,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(300, lifetimeSeconds),
                Revoked = false
            };

            lock (m_Sync)
            {
                m_Document.RefreshTokens[HashOpaqueToken(token)] = grant;
                SaveLocked();
            }

            return new NexRefreshTokenIssue(token, CloneRefreshGrant(grant));
        }

        public bool TryRotateRefreshToken(
            string refreshToken,
            string clientId,
            int lifetimeSeconds,
            out NexRefreshGrant previousGrant,
            out NexRefreshTokenIssue replacement)
        {
            previousGrant = null;
            replacement = null;

            if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId))
                return false;

            string tokenHash = HashOpaqueToken(refreshToken);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (m_Sync)
            {
                if (!m_Document.RefreshTokens.TryGetValue(tokenHash, out NexRefreshGrant stored) ||
                    stored.Revoked ||
                    stored.ExpiresAt <= now ||
                    !string.Equals(stored.ClientId, clientId, StringComparison.Ordinal))
                    return false;

                stored.Revoked = true;
                previousGrant = CloneRefreshGrant(stored);

                string newToken = GenerateOpaqueToken(64);
                NexRefreshGrant next = new NexRefreshGrant
                {
                    ClientId = stored.ClientId,
                    Subject = stored.Subject,
                    Scopes = (stored.Scopes ?? Array.Empty<string>()).ToArray(),
                    SecurityStamp = stored.SecurityStamp,
                    CreatedAt = now,
                    ExpiresAt = now + Math.Max(300, lifetimeSeconds),
                    Revoked = false
                };

                m_Document.RefreshTokens[HashOpaqueToken(newToken)] = next;
                SaveLocked();
                replacement = new NexRefreshTokenIssue(newToken, CloneRefreshGrant(next));
                return true;
            }
        }

        public bool RevokeRefreshToken(string refreshToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                return false;

            lock (m_Sync)
            {
                if (!m_Document.RefreshTokens.TryGetValue(HashOpaqueToken(refreshToken), out NexRefreshGrant grant))
                    return false;

                grant.Revoked = true;
                SaveLocked();
                return true;
            }
        }

        public int RevokeSubjectRefreshTokens(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
                return 0;

            int count = 0;
            lock (m_Sync)
            {
                foreach (NexRefreshGrant grant in m_Document.RefreshTokens.Values)
                {
                    if (!grant.Revoked &&
                        string.Equals(grant.Subject, subject, StringComparison.OrdinalIgnoreCase))
                    {
                        grant.Revoked = true;
                        count++;
                    }
                }

                if (count > 0)
                    SaveLocked();
            }

            return count;
        }

        public void RevokeAccessToken(string tokenId, long expiresAt)
        {
            if (string.IsNullOrWhiteSpace(tokenId))
                return;

            lock (m_Sync)
            {
                m_Document.RevokedAccessTokens[tokenId] = expiresAt;
                CleanupExpiredLocked();
                SaveLocked();
            }
        }

        public bool IsAccessTokenRevoked(string tokenId)
        {
            if (string.IsNullOrWhiteSpace(tokenId))
                return true;

            lock (m_Sync)
            {
                CleanupExpiredLocked();
                return m_Document.RevokedAccessTokens.ContainsKey(tokenId);
            }
        }

        private NexOAuthClient GetRequiredEnabledClient(string clientId)
        {
            NexOAuthClient client = GetClient(clientId);
            if (client == null || !client.Enabled)
                throw new InvalidOperationException("OAuth client is not enabled.");
            return client;
        }

        private static string[] ValidateScopes(NexOAuthClient client, IEnumerable<string> scopes)
        {
            string[] requested = NormalizeValues(scopes);
            HashSet<string> allowed = new HashSet<string>(
                client.AllowedScopes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (requested.Any(x => !allowed.Contains(x)))
                throw new InvalidOperationException("Requested scope is not allowed for this client.");

            return requested;
        }

        private AuthStoreDocument Load()
        {
            if (!File.Exists(m_Path))
                return new AuthStoreDocument();

            try
            {
                string json = File.ReadAllText(m_Path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AuthStoreDocument>(json) ?? new AuthStoreDocument();
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Unable to load NexVerse auth store: " + m_Path, e);
            }
        }

        private void CleanupExpired()
        {
            lock (m_Sync)
            {
                if (CleanupExpiredLocked())
                    SaveLocked();
            }
        }

        private bool CleanupExpiredLocked()
        {
            bool changed = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (string key in m_Document.AuthorizationCodes
                         .Where(x => x.Value == null || x.Value.ExpiresAt <= now)
                         .Select(x => x.Key)
                         .ToArray())
            {
                m_Document.AuthorizationCodes.Remove(key);
                changed = true;
            }

            foreach (string key in m_Document.RefreshTokens
                         .Where(x => x.Value == null || x.Value.ExpiresAt <= now)
                         .Select(x => x.Key)
                         .ToArray())
            {
                m_Document.RefreshTokens.Remove(key);
                changed = true;
            }

            foreach (string key in m_Document.RevokedAccessTokens
                         .Where(x => x.Value <= now)
                         .Select(x => x.Key)
                         .ToArray())
            {
                m_Document.RevokedAccessTokens.Remove(key);
                changed = true;
            }

            return changed;
        }

        private void SaveLocked()
        {
            string directory = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temp = m_Path + ".tmp";
            string json = JsonSerializer.Serialize(
                m_Document,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, m_Path, true);
        }

        private static string[] NormalizeValues(IEnumerable<string> values)
        {
            return (values ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GenerateOpaqueToken(int bytes)
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
        }

        private static string HashOpaqueToken(string value)
        {
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                .ToLowerInvariant();
        }

        private static void HashSecret(string secret, out string salt, out string hash)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(24);
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
                secret,
                saltBytes,
                SecretIterations,
                HashAlgorithmName.SHA256,
                32);

            salt = Convert.ToBase64String(saltBytes);
            hash = Convert.ToBase64String(derived);
        }

        private static bool VerifySecret(string secret, string salt, string hash)
        {
            try
            {
                byte[] saltBytes = Convert.FromBase64String(salt);
                byte[] expected = Convert.FromBase64String(hash);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    secret,
                    saltBytes,
                    SecretIterations,
                    HashAlgorithmName.SHA256,
                    expected.Length);

                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }

        private static bool VerifyPkceS256(string verifier, string expectedChallenge)
        {
            string challenge = Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            byte[] a = Encoding.ASCII.GetBytes(challenge);
            byte[] b = Encoding.ASCII.GetBytes(expectedChallenge ?? string.Empty);

            return a.Length == b.Length &&
                   CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static NexOAuthClient CloneClient(NexOAuthClient value)
        {
            return new NexOAuthClient
            {
                ClientId = value.ClientId,
                Name = value.Name,
                ClientType = value.ClientType,
                RedirectUris = (value.RedirectUris ?? Array.Empty<string>()).ToArray(),
                AllowedScopes = (value.AllowedScopes ?? Array.Empty<string>()).ToArray(),
                SecretSalt = value.SecretSalt,
                SecretHash = value.SecretHash,
                Enabled = value.Enabled,
                SecurityStamp = value.SecurityStamp,
                CreatedAt = value.CreatedAt,
                UpdatedAt = value.UpdatedAt
            };
        }

        private static NexAuthorizationGrant CloneAuthorizationGrant(NexAuthorizationGrant value)
        {
            return new NexAuthorizationGrant
            {
                ClientId = value.ClientId,
                Subject = value.Subject,
                RedirectUri = value.RedirectUri,
                Scopes = (value.Scopes ?? Array.Empty<string>()).ToArray(),
                CodeChallenge = value.CodeChallenge,
                CodeChallengeMethod = value.CodeChallengeMethod,
                Nonce = value.Nonce,
                SecurityStamp = value.SecurityStamp,
                ExpiresAt = value.ExpiresAt
            };
        }

        private static NexRefreshGrant CloneRefreshGrant(NexRefreshGrant value)
        {
            return new NexRefreshGrant
            {
                ClientId = value.ClientId,
                Subject = value.Subject,
                Scopes = (value.Scopes ?? Array.Empty<string>()).ToArray(),
                SecurityStamp = value.SecurityStamp,
                CreatedAt = value.CreatedAt,
                ExpiresAt = value.ExpiresAt,
                Revoked = value.Revoked
            };
        }

        private sealed class AuthStoreDocument
        {
            public List<NexOAuthClient> Clients { get; set; } = new List<NexOAuthClient>();
            public Dictionary<string, NexAuthorizationGrant> AuthorizationCodes { get; set; } =
                new Dictionary<string, NexAuthorizationGrant>(StringComparer.Ordinal);
            public Dictionary<string, NexRefreshGrant> RefreshTokens { get; set; } =
                new Dictionary<string, NexRefreshGrant>(StringComparer.Ordinal);
            public Dictionary<string, long> RevokedAccessTokens { get; set; } =
                new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
