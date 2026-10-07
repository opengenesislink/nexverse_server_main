// SPDX-License-Identifier: BSD-3-Clause

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Framework
{
    public static class OfflineImMailRelayToken
    {
        private const string Prefix = "r";
        private const int NonceBytes = 6;
        private const int SignatureBytes = 12;

        public static string Create(
            UUID targetAgentId,
            string authorizedEmail,
            DateTimeOffset expiresAt,
            string signingKey)
        {
            ValidateSigningKey(signingKey);

            string target = Base64Url(targetAgentId.Guid.ToByteArray());
            string expiry = ToBase36(expiresAt.ToUnixTimeSeconds());
            string nonce = Base64Url(RandomNumberGenerator.GetBytes(NonceBytes));
            string signature = Sign(target, expiry, nonce, NormalizeEmail(authorizedEmail), signingKey);

            return string.Join("-", Prefix, target, expiry, nonce, signature);
        }

        public static bool TryValidate(
            string localPart,
            string authorizedEmail,
            string signingKey,
            DateTimeOffset now,
            TimeSpan maximumFutureLifetime,
            out UUID targetAgentId,
            out DateTimeOffset expiresAt)
        {
            targetAgentId = UUID.Zero;
            expiresAt = default;

            if (string.IsNullOrWhiteSpace(localPart) ||
                string.IsNullOrWhiteSpace(authorizedEmail) ||
                string.IsNullOrWhiteSpace(signingKey))
                return false;

            string[] parts = localPart.Split('-');
            if (parts.Length != 5 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
                return false;

            if (!TryBase64UrlDecode(parts[1], out byte[] targetBytes) || targetBytes.Length != 16)
                return false;

            if (!TryFromBase36(parts[2], out long expiryUnix))
                return false;

            DateTimeOffset expiry;
            try
            {
                expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (expiry <= now || expiry > now.Add(maximumFutureLifetime))
                return false;

            string expected = Sign(parts[1], parts[2], parts[3], NormalizeEmail(authorizedEmail), signingKey);
            if (!FixedTimeEquals(parts[4], expected))
                return false;

            try
            {
                targetAgentId = new UUID(new Guid(targetBytes));
                expiresAt = expiry;
                return !targetAgentId.IsZero();
            }
            catch
            {
                targetAgentId = UUID.Zero;
                return false;
            }
        }

        public static string NormalizeEmail(string email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }

        public static bool IsSigningKeyStrongEnough(string signingKey)
        {
            return !string.IsNullOrWhiteSpace(signingKey) &&
                   Encoding.UTF8.GetByteCount(signingKey) >= 32;
        }

        private static void ValidateSigningKey(string signingKey)
        {
            if (!IsSigningKeyStrongEnough(signingKey))
                throw new ArgumentException("Offline IM relay signing key must contain at least 32 UTF-8 bytes.", nameof(signingKey));
        }

        private static string Sign(
            string target,
            string expiry,
            string nonce,
            string normalizedEmail,
            string signingKey)
        {
            string canonical = target + "|" + expiry + "|" + nonce + "|" + normalizedEmail;
            byte[] key = Encoding.UTF8.GetBytes(signingKey);
            byte[] data = Encoding.UTF8.GetBytes(canonical);

            using HMACSHA256 hmac = new HMACSHA256(key);
            byte[] digest = hmac.ComputeHash(data);
            byte[] truncated = new byte[SignatureBytes];
            Buffer.BlockCopy(digest, 0, truncated, 0, truncated.Length);
            return Base64Url(truncated);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (!TryBase64UrlDecode(left, out byte[] leftBytes) ||
                !TryBase64UrlDecode(right, out byte[] rightBytes) ||
                leftBytes.Length != rightBytes.Length)
                return false;

            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool TryBase64UrlDecode(string value, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
                case 1:
                    return false;
            }

            try
            {
                data = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string ToBase36(long value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (value == 0)
                return "0";

            StringBuilder result = new StringBuilder();
            while (value > 0)
            {
                result.Insert(0, chars[(int)(value % 36)]);
                value /= 36;
            }

            return result.ToString();
        }

        private static bool TryFromBase36(string value, out long result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (char raw in value)
            {
                char ch = char.ToLowerInvariant(raw);
                int digit = ch >= '0' && ch <= '9'
                    ? ch - '0'
                    : ch >= 'a' && ch <= 'z'
                        ? ch - 'a' + 10
                        : -1;

                if (digit < 0 || digit >= 36)
                    return false;

                try
                {
                    checked
                    {
                        result = (result * 36) + digit;
                    }
                }
                catch (OverflowException)
                {
                    result = 0;
                    return false;
                }
            }

            return true;
        }
    }
}
