// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Security
{
    public enum NexIdempotencyBeginState
    {
        New,
        Replay,
        Conflict,
        InProgress
    }

    public sealed class NexIdempotencyResponse
    {
        public int StatusCode { get; }
        public string ContentType { get; }
        public byte[] Body { get; }

        public NexIdempotencyResponse(
            int statusCode,
            string contentType,
            byte[] body)
        {
            StatusCode = statusCode;
            ContentType = contentType ?? "application/json; charset=utf-8";
            Body = body == null ? Array.Empty<byte>() : body.ToArray();
        }
    }

    public sealed class NexIdempotencyBeginResult
    {
        public NexIdempotencyBeginState State { get; }
        public NexIdempotencyResponse Response { get; }

        public NexIdempotencyBeginResult(
            NexIdempotencyBeginState state,
            NexIdempotencyResponse response = null)
        {
            State = state;
            Response = response;
        }
    }

    public interface INexIdempotencyStore
    {
        NexIdempotencyBeginResult TryBegin(
            string scope,
            string key,
            string requestHash,
            int ttlSeconds);

        void Complete(
            string scope,
            string key,
            string requestHash,
            int statusCode,
            string contentType,
            byte[] responseBody,
            int ttlSeconds);

        void Abort(
            string scope,
            string key,
            string requestHash);
    }

    public sealed class PersistentNexIdempotencyStore : INexIdempotencyStore
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private StoreDocument m_Document;

        public PersistentNexIdempotencyStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "Idempotency store path is required.",
                    nameof(path));

            m_Path = Path.GetFullPath(path);
            m_Document = Load();
            Normalize();
            CleanupExpiredLocked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            // Pending reservations are intentionally process-local.  A process
            // restart must not preserve a half-finished reservation.
            foreach (string key in m_Document.Entries
                         .Where(x => !x.Value.Completed)
                         .Select(x => x.Key)
                         .ToArray())
            {
                m_Document.Entries.Remove(key);
            }

            SaveLocked();
        }

        public NexIdempotencyBeginResult TryBegin(
            string scope,
            string key,
            string requestHash,
            int ttlSeconds)
        {
            Validate(scope, key, requestHash);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string composite = Composite(scope, key);

            lock (m_Sync)
            {
                bool cleaned = CleanupExpiredLocked(now);

                if (m_Document.Entries.TryGetValue(
                    composite,
                    out StoreEntry existing))
                {
                    if (!string.Equals(
                        existing.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                    {
                        if (cleaned)
                            SaveLocked();

                        return new NexIdempotencyBeginResult(
                            NexIdempotencyBeginState.Conflict);
                    }

                    if (!existing.Completed)
                    {
                        if (cleaned)
                            SaveLocked();

                        return new NexIdempotencyBeginResult(
                            NexIdempotencyBeginState.InProgress);
                    }

                    if (cleaned)
                        SaveLocked();

                    return new NexIdempotencyBeginResult(
                        NexIdempotencyBeginState.Replay,
                        new NexIdempotencyResponse(
                            existing.StatusCode,
                            existing.ContentType,
                            existing.ResponseBody));
                }

                m_Document.Entries[composite] = new StoreEntry
                {
                    Scope = scope,
                    Key = key,
                    RequestHash = requestHash,
                    Completed = false,
                    ExpiresAt = now + Math.Max(60, ttlSeconds)
                };

                // Pending reservations are not persisted. They only serialize
                // concurrent requests inside the current server process.
                if (cleaned)
                    SaveLocked();

                return new NexIdempotencyBeginResult(
                    NexIdempotencyBeginState.New);
            }
        }

        public void Complete(
            string scope,
            string key,
            string requestHash,
            int statusCode,
            string contentType,
            byte[] responseBody,
            int ttlSeconds)
        {
            Validate(scope, key, requestHash);
            string composite = Composite(scope, key);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (m_Sync)
            {
                if (!m_Document.Entries.TryGetValue(
                    composite,
                    out StoreEntry entry) ||
                    !string.Equals(
                        entry.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                    return;

                entry.Completed = true;
                entry.StatusCode = statusCode;
                entry.ContentType =
                    contentType ?? "application/json; charset=utf-8";
                entry.ResponseBody =
                    responseBody == null
                        ? Array.Empty<byte>()
                        : responseBody.ToArray();
                entry.ExpiresAt =
                    now + Math.Max(60, ttlSeconds);

                CleanupExpiredLocked(now);
                SaveLocked();
            }
        }

        public void Abort(
            string scope,
            string key,
            string requestHash)
        {
            Validate(scope, key, requestHash);
            string composite = Composite(scope, key);

            lock (m_Sync)
            {
                if (!m_Document.Entries.TryGetValue(
                    composite,
                    out StoreEntry entry) ||
                    entry.Completed ||
                    !string.Equals(
                        entry.RequestHash,
                        requestHash,
                        StringComparison.Ordinal))
                    return;

                m_Document.Entries.Remove(composite);
            }
        }

        private StoreDocument Load()
        {
            if (!File.Exists(m_Path))
                return new StoreDocument();

            try
            {
                return JsonSerializer.Deserialize<StoreDocument>(
                    File.ReadAllText(m_Path, Encoding.UTF8),
                    s_Json) ?? new StoreDocument();
            }
            catch (Exception e)
            {
                throw new InvalidDataException(
                    "Unable to load NexVerse idempotency store: " +
                    m_Path,
                    e);
            }
        }

        private void Normalize()
        {
            m_Document ??= new StoreDocument();
            m_Document.Entries ??=
                new Dictionary<string, StoreEntry>(
                    StringComparer.Ordinal);
        }

        private bool CleanupExpiredLocked(long now)
        {
            bool changed = false;

            foreach (string key in m_Document.Entries
                         .Where(x => x.Value == null ||
                                     x.Value.ExpiresAt <= now)
                         .Select(x => x.Key)
                         .ToArray())
            {
                m_Document.Entries.Remove(key);
                changed = true;
            }

            return changed;
        }

        private void SaveLocked()
        {
            string directory = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            StoreDocument persisted = new StoreDocument
            {
                Entries = m_Document.Entries
                    .Where(x => x.Value != null && x.Value.Completed)
                    .ToDictionary(
                        x => x.Key,
                        x => x.Value,
                        StringComparer.Ordinal)
            };

            string temp = m_Path + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(persisted, s_Json),
                new UTF8Encoding(false));
            File.Move(temp, m_Path, true);
        }

        private static string Composite(
            string scope,
            string key)
        {
            return scope + "\u001f" + key;
        }

        private static void Validate(
            string scope,
            string key,
            string requestHash)
        {
            if (string.IsNullOrWhiteSpace(scope))
                throw new ArgumentException(
                    "Idempotency scope is required.",
                    nameof(scope));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException(
                    "Idempotency key is required.",
                    nameof(key));
            if (string.IsNullOrWhiteSpace(requestHash))
                throw new ArgumentException(
                    "Idempotency request hash is required.",
                    nameof(requestHash));
        }

        private sealed class StoreDocument
        {
            public Dictionary<string, StoreEntry> Entries { get; set; } =
                new Dictionary<string, StoreEntry>(
                    StringComparer.Ordinal);
        }

        private sealed class StoreEntry
        {
            public string Scope { get; set; } = string.Empty;
            public string Key { get; set; } = string.Empty;
            public string RequestHash { get; set; } = string.Empty;
            public bool Completed { get; set; }
            public int StatusCode { get; set; }
            public string ContentType { get; set; } =
                "application/json; charset=utf-8";
            public byte[] ResponseBody { get; set; } =
                Array.Empty<byte>();
            public long ExpiresAt { get; set; }
        }
    }
}
