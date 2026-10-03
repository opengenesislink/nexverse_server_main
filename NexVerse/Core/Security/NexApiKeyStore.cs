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
    public sealed class NexApiKeyRecord
    {
        public string KeyId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public string SecretSalt { get; set; } = string.Empty;
        public string SecretHash { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long LastUsedAt { get; set; }
    }

    public sealed class NexApiKeyRegistration
    {
        public NexApiKeyRecord Record { get; }
        public string ApiKey { get; }

        public NexApiKeyRegistration(
            NexApiKeyRecord record,
            string apiKey)
        {
            Record = record;
            ApiKey = apiKey ?? string.Empty;
        }
    }

    public interface INexApiKeyStore
    {
        NexApiKeyRegistration Create(
            string name,
            IEnumerable<string> scopes);

        IReadOnlyList<NexApiKeyRecord> List();
        bool SetEnabled(string keyId, bool enabled);
        bool Delete(string keyId);
        bool TryValidate(
            string apiKey,
            out NexApiKeyRecord record);
    }

    public sealed class PersistentNexApiKeyStore : INexApiKeyStore
    {
        private const int SecretIterations = 120000;

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private StoreDocument m_Document;

        public PersistentNexApiKeyStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "API key store path is required.",
                    nameof(path));

            m_Path = Path.GetFullPath(path);
            m_Document = Load();
            m_Document ??= new StoreDocument();
            m_Document.Keys ??=
                new List<NexApiKeyRecord>();
        }

        public NexApiKeyRegistration Create(
            string name,
            IEnumerable<string> scopes)
        {
            string[] normalizedScopes = NormalizeScopes(scopes);
            if (normalizedScopes.Length == 0)
                throw new ArgumentException(
                    "At least one API key scope is required.",
                    nameof(scopes));

            HashSet<string> supportedScopes = new HashSet<string>(
                new[]
                {
                    NexScopes.UsersRead,
                    NexScopes.UsersWrite,
                    NexScopes.InventoryRead,
                    NexScopes.InventoryWrite,
                    NexScopes.FriendsManage,
                    NexScopes.RegionsRead,
                    NexScopes.RegionsManage,
                    NexScopes.SimulatorsRead,
                    NexScopes.SimulatorsManage,
                    NexScopes.StatisticsRead,
                    NexScopes.EstatesRead,
                    NexScopes.EstatesManage,
                    NexScopes.EconomyRead,
                    NexScopes.EconomyTransfer
                },
                StringComparer.OrdinalIgnoreCase);

            foreach (string scope in normalizedScopes)
            {
                if (!supportedScopes.Contains(scope))
                {
                    throw new ArgumentException(
                        "API keys may only receive explicit supported machine scopes; wildcard, admin and interactive identity scopes are forbidden.",
                        nameof(scopes));
                }
            }

            string keyId =
                "nxk_" + Guid.NewGuid().ToString("N");
            string secret =
                Base64UrlEncode(
                    RandomNumberGenerator.GetBytes(48));

            HashSecret(
                secret,
                out string salt,
                out string hash);

            long now =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            NexApiKeyRecord record = new NexApiKeyRecord
            {
                KeyId = keyId,
                Name = string.IsNullOrWhiteSpace(name)
                    ? "NexVerse API key"
                    : name.Trim(),
                Scopes = normalizedScopes,
                SecretSalt = salt,
                SecretHash = hash,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            lock (m_Sync)
            {
                m_Document.Keys.Add(record);
                SaveLocked();
            }

            return new NexApiKeyRegistration(
                Clone(record),
                keyId + "." + secret);
        }

        public IReadOnlyList<NexApiKeyRecord> List()
        {
            lock (m_Sync)
            {
                return m_Document.Keys
                    .Select(Clone)
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.KeyId, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public bool SetEnabled(
            string keyId,
            bool enabled)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                return false;

            lock (m_Sync)
            {
                NexApiKeyRecord record =
                    m_Document.Keys.FirstOrDefault(x =>
                        string.Equals(
                            x.KeyId,
                            keyId,
                            StringComparison.Ordinal));

                if (record == null)
                    return false;

                record.Enabled = enabled;
                record.UpdatedAt =
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveLocked();
                return true;
            }
        }

        public bool Delete(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId)) return false;
            lock (m_Sync)
            {
                int removed = m_Document.Keys.RemoveAll(x => string.Equals(x.KeyId, keyId, StringComparison.Ordinal));
                if (removed > 0) SaveLocked();
                return removed > 0;
            }
        }

        public bool TryValidate(
            string apiKey,
            out NexApiKeyRecord record)
        {
            record = null;

            if (string.IsNullOrWhiteSpace(apiKey))
                return false;

            int separator = apiKey.IndexOf('.');
            if (separator <= 0 ||
                separator == apiKey.Length - 1)
                return false;

            string keyId = apiKey.Substring(0, separator);
            string secret = apiKey.Substring(separator + 1);

            lock (m_Sync)
            {
                NexApiKeyRecord stored =
                    m_Document.Keys.FirstOrDefault(x =>
                        string.Equals(
                            x.KeyId,
                            keyId,
                            StringComparison.Ordinal));

                if (stored == null ||
                    !stored.Enabled ||
                    !VerifySecret(
                        secret,
                        stored.SecretSalt,
                        stored.SecretHash))
                    return false;

                stored.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveLocked();
                record = Clone(stored);
                return true;
            }
        }

        private StoreDocument Load()
        {
            if (!File.Exists(m_Path))
                return new StoreDocument();

            try
            {
                return JsonSerializer.Deserialize<StoreDocument>(
                    File.ReadAllText(m_Path, Encoding.UTF8))
                    ?? new StoreDocument();
            }
            catch (Exception e)
            {
                throw new InvalidDataException(
                    "Unable to load NexVerse API key store: " +
                    m_Path,
                    e);
            }
        }

        private void SaveLocked()
        {
            string directory =
                Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temp = m_Path + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    m_Document,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                new UTF8Encoding(false));
            File.Move(temp, m_Path, true);
        }

        private static string[] NormalizeScopes(
            IEnumerable<string> scopes)
        {
            return (scopes ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void HashSecret(
            string secret,
            out string salt,
            out string hash)
        {
            byte[] saltBytes =
                RandomNumberGenerator.GetBytes(24);

            byte[] derived =
                Rfc2898DeriveBytes.Pbkdf2(
                    secret,
                    saltBytes,
                    SecretIterations,
                    HashAlgorithmName.SHA256,
                    32);

            salt = Convert.ToBase64String(saltBytes);
            hash = Convert.ToBase64String(derived);
        }

        private static bool VerifySecret(
            string secret,
            string salt,
            string hash)
        {
            try
            {
                byte[] saltBytes =
                    Convert.FromBase64String(salt);
                byte[] expected =
                    Convert.FromBase64String(hash);
                byte[] actual =
                    Rfc2898DeriveBytes.Pbkdf2(
                        secret,
                        saltBytes,
                        SecretIterations,
                        HashAlgorithmName.SHA256,
                        expected.Length);

                return CryptographicOperations
                    .FixedTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }

        private static string Base64UrlEncode(
            byte[] value)
        {
            return Convert
                .ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static NexApiKeyRecord Clone(
            NexApiKeyRecord value)
        {
            return new NexApiKeyRecord
            {
                KeyId = value.KeyId,
                Name = value.Name,
                Scopes =
                    (value.Scopes ??
                     Array.Empty<string>())
                    .ToArray(),
                SecretSalt = value.SecretSalt,
                SecretHash = value.SecretHash,
                Enabled = value.Enabled,
                CreatedAt = value.CreatedAt,
                UpdatedAt = value.UpdatedAt
            };
        }

        private sealed class StoreDocument
        {
            public List<NexApiKeyRecord> Keys { get; set; } =
                new List<NexApiKeyRecord>();
        }
    }
}
