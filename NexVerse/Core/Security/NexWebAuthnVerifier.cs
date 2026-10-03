// SPDX-License-Identifier: MPL-2.0

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Security
{
    public sealed class NexWebAuthnVerifier
    {
        private readonly string m_RpId;
        private readonly string m_Origin;

        public NexWebAuthnVerifier(string rpId, string origin)
        {
            m_RpId = (rpId ?? string.Empty).Trim().ToLowerInvariant();
            m_Origin = (origin ?? string.Empty).TrimEnd('/');
        }

        public bool VerifyAssertion(
            NexPasskeyCredential credential,
            string expectedChallenge,
            string clientDataJsonBase64Url,
            string authenticatorDataBase64Url,
            string signatureBase64Url,
            out long signCount)
        {
            signCount = 0;
            try
            {
                byte[] clientData = Decode(clientDataJsonBase64Url);
                using JsonDocument client = JsonDocument.Parse(clientData);
                JsonElement root = client.RootElement;
                if (root.GetProperty("type").GetString() != "webauthn.get") return false;
                if (root.GetProperty("challenge").GetString() != expectedChallenge) return false;
                if (!string.Equals(root.GetProperty("origin").GetString()?.TrimEnd('/'), m_Origin, StringComparison.OrdinalIgnoreCase)) return false;

                byte[] authData = Decode(authenticatorDataBase64Url);
                if (authData.Length < 37) return false;
                byte[] expectedRp = SHA256.HashData(Encoding.UTF8.GetBytes(m_RpId));
                if (!CryptographicOperations.FixedTimeEquals(expectedRp, authData.AsSpan(0, 32))) return false;
                byte flags = authData[32];
                if ((flags & 0x01) == 0) return false;
                signCount = ((long)authData[33] << 24) | ((long)authData[34] << 16) | ((long)authData[35] << 8) | authData[36];

                byte[] clientHash = SHA256.HashData(clientData);
                byte[] signed = new byte[authData.Length + clientHash.Length];
                Buffer.BlockCopy(authData, 0, signed, 0, authData.Length);
                Buffer.BlockCopy(clientHash, 0, signed, authData.Length, clientHash.Length);

                byte[] key = Decode(credential.PublicKey);
                byte[] signature = Decode(signatureBase64Url);
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                return ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] Decode(string value)
        {
            string s = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return Convert.FromBase64String(s);
        }
    }
}
