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
    public sealed class NexSecurityEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Subject { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public bool Success { get; set; }
    }

    public sealed class NexSecuritySession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Subject { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public long LastSeenAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public bool Revoked { get; set; }
    }

    public interface INexSecurityStore
    {
        NexSecuritySession CreateSession(string subject, string clientId);
        IReadOnlyList<NexSecuritySession> ListSessions(string subject);
        bool RevokeSession(string subject, string sessionId);
        void Record(string subject, string type, bool success, string sessionId = null);
        IReadOnlyList<NexSecurityEvent> History(string subject, int limit);
        string EnsureTotpSecret(string subject);
        bool IsTotpEnabled(string subject);
        bool VerifyTotp(string subject, string code);
        bool DisableTotp(string subject);
    }

    public sealed class PersistentNexSecurityStore : INexSecurityStore
    {
        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private Document m_Data;

        public PersistentNexSecurityStore(string path)
        {
            m_Path = Path.GetFullPath(path);
            m_Data = Load();
        }

        public NexSecuritySession CreateSession(string subject, string clientId)
        {
            NexSecuritySession session = new NexSecuritySession { Subject = subject ?? string.Empty, ClientId = clientId ?? string.Empty };
            lock (m_Sync) { m_Data.Sessions.Add(session); Save(); }
            Record(subject, "session.created", true, session.Id);
            return session;
        }

        public IReadOnlyList<NexSecuritySession> ListSessions(string subject)
        {
            lock (m_Sync) return m_Data.Sessions.Where(x => x.Subject == subject).OrderByDescending(x => x.LastSeenAt).ToArray();
        }

        public bool RevokeSession(string subject, string sessionId)
        {
            lock (m_Sync)
            {
                NexSecuritySession session = m_Data.Sessions.FirstOrDefault(x => x.Subject == subject && x.Id == sessionId);
                if (session == null) return false;
                session.Revoked = true;
                Save();
            }
            Record(subject, "session.revoked", true, sessionId);
            return true;
        }

        public void Record(string subject, string type, bool success, string sessionId = null)
        {
            lock (m_Sync)
            {
                m_Data.Events.Add(new NexSecurityEvent { Subject = subject ?? string.Empty, Type = type ?? string.Empty, Success = success, SessionId = sessionId ?? string.Empty });
                if (m_Data.Events.Count > 10000) m_Data.Events.RemoveRange(0, m_Data.Events.Count - 10000);
                Save();
            }
        }

        public IReadOnlyList<NexSecurityEvent> History(string subject, int limit)
        {
            limit = Math.Max(1, Math.Min(limit, 200));
            lock (m_Sync) return m_Data.Events.Where(x => x.Subject == subject).OrderByDescending(x => x.Timestamp).Take(limit).ToArray();
        }

        public string EnsureTotpSecret(string subject)
        {
            lock (m_Sync)
            {
                if (m_Data.Totp.TryGetValue(subject, out string existing)) return existing;
                byte[] secret = new byte[20];
                RandomNumberGenerator.Fill(secret);
                string value = Convert.ToBase64String(secret);
                m_Data.Totp[subject] = value;
                Save();
                return value;
            }
        }

        public bool IsTotpEnabled(string subject)
        {
            lock (m_Sync) return m_Data.Totp.ContainsKey(subject);
        }

        public bool VerifyTotp(string subject, string code)
        {
            string encoded;
            lock (m_Sync) if (!m_Data.Totp.TryGetValue(subject, out encoded)) return false;
            if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
            byte[] key = Convert.FromBase64String(encoded);
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            for (long i = step - 1; i <= step + 1; i++)
                if (FixedEquals(Code(key, i), code)) return true;
            return false;
        }

        public bool DisableTotp(string subject)
        {
            lock (m_Sync) { bool removed = m_Data.Totp.Remove(subject); if (removed) Save(); return removed; }
        }

        private static string Code(byte[] key, long counter)
        {
            byte[] data = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(data);
            using HMACSHA1 hmac = new HMACSHA1(key);
            byte[] hash = hmac.ComputeHash(data);
            int offset = hash[hash.Length - 1] & 15;
            int binary = ((hash[offset] & 127) << 24) | ((hash[offset + 1] & 255) << 16) | ((hash[offset + 2] & 255) << 8) | (hash[offset + 3] & 255);
            return (binary % 1000000).ToString("D6");
        }

        private static bool FixedEquals(string a, string b)
        {
            byte[] x = Encoding.ASCII.GetBytes(a ?? string.Empty), y = Encoding.ASCII.GetBytes(b ?? string.Empty);
            return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
        }

        private Document Load()
        {
            try
            {
                if (File.Exists(m_Path)) return JsonSerializer.Deserialize<Document>(File.ReadAllText(m_Path)) ?? new Document();
            }
            catch { }
            return new Document();
        }

        private void Save()
        {
            string dir = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = m_Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(m_Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, m_Path, true);
        }

        private sealed class Document
        {
            public List<NexSecuritySession> Sessions { get; set; } = new List<NexSecuritySession>();
            public List<NexSecurityEvent> Events { get; set; } = new List<NexSecurityEvent>();
            public Dictionary<string, string> Totp { get; set; } = new Dictionary<string, string>();
        }
    }
}
