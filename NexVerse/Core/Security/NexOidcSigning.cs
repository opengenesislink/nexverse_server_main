// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Security
{
    public interface INexOidcSigningService
    {
        string Issuer { get; }
        int LifetimeSeconds { get; }
        string KeyId { get; }
        string IssueIdentityToken(
            string subject,
            string clientId,
            string nonce,
            int securityStamp);
        string GetJwksJson();
    }

    public sealed class PersistentEs256OidcSigningService : INexOidcSigningService, IDisposable
    {
        private readonly ECDsa m_Key;

        public string Issuer { get; }
        public int LifetimeSeconds { get; }
        public string KeyId { get; }

        public PersistentEs256OidcSigningService(
            string issuer,
            string privateKeyPath,
            int lifetimeSeconds)
        {
            if (string.IsNullOrWhiteSpace(issuer))
                throw new ArgumentException("OIDC issuer is required.", nameof(issuer));
            if (string.IsNullOrWhiteSpace(privateKeyPath))
                throw new ArgumentException("OIDC signing-key path is required.", nameof(privateKeyPath));

            Issuer = issuer.TrimEnd('/');
            LifetimeSeconds = Math.Max(60, lifetimeSeconds);

            string fullPath = Path.GetFullPath(privateKeyPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            m_Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            if (File.Exists(fullPath))
            {
                string pem = File.ReadAllText(fullPath, Encoding.UTF8);
                m_Key.ImportFromPem(pem);
            }
            else
            {
                string pem = m_Key.ExportPkcs8PrivateKeyPem();
                string temp = fullPath + ".tmp";
                File.WriteAllText(temp, pem, new UTF8Encoding(false));
                File.Move(temp, fullPath, true);
            }

            ECParameters publicParameters = m_Key.ExportParameters(false);
            byte[] keyMaterial = new byte[
                publicParameters.Q.X.Length + publicParameters.Q.Y.Length];

            Buffer.BlockCopy(publicParameters.Q.X, 0, keyMaterial, 0, publicParameters.Q.X.Length);
            Buffer.BlockCopy(
                publicParameters.Q.Y,
                0,
                keyMaterial,
                publicParameters.Q.X.Length,
                publicParameters.Q.Y.Length);

            KeyId = Base64UrlEncode(SHA256.HashData(keyMaterial)).Substring(0, 22);
        }

        public string IssueIdentityToken(
            string subject,
            string clientId,
            string nonce,
            int securityStamp)
        {
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("OIDC subject is required.", nameof(subject));
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("OIDC client ID is required.", nameof(clientId));

            long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expiresAt = issuedAt + LifetimeSeconds;

            byte[] header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                typ = "JWT",
                kid = KeyId
            });

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = Issuer,
                aud = clientId,
                sub = subject,
                iat = issuedAt,
                exp = expiresAt,
                jti = Guid.NewGuid().ToString("N"),
                nonce = nonce ?? string.Empty,
                nxs = securityStamp
            });

            string unsignedToken =
                Base64UrlEncode(header) + "." + Base64UrlEncode(payload);

            byte[] signature = m_Key.SignData(
                Encoding.ASCII.GetBytes(unsignedToken),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            return unsignedToken + "." + Base64UrlEncode(signature);
        }

        public string GetJwksJson()
        {
            ECParameters parameters = m_Key.ExportParameters(false);

            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "EC",
                        use = "sig",
                        crv = "P-256",
                        alg = "ES256",
                        kid = KeyId,
                        x = Base64UrlEncode(parameters.Q.X),
                        y = Base64UrlEncode(parameters.Q.Y)
                    }
                }
            });
        }

        public void Dispose()
        {
            m_Key.Dispose();
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
