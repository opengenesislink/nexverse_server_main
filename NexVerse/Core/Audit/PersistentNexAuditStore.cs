// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Audit
{
    public sealed class PersistentNexAuditStore : INexAuditStore
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions();

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private readonly List<NexAuditEvent> m_Events =
            new List<NexAuditEvent>();

        public PersistentNexAuditStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "Audit store path is required.",
                    nameof(path));

            m_Path = Path.GetFullPath(path);
            Load();
        }

        public void Record(NexAuditEvent auditEvent)
        {
            if (auditEvent == null)
                return;

            AuditRecord record = AuditRecord.FromEvent(auditEvent);
            string line = JsonSerializer.Serialize(record, s_Json);

            lock (m_Sync)
            {
                string directory = Path.GetDirectoryName(m_Path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                using FileStream stream = new FileStream(
                    m_Path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using StreamWriter writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false));

                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(true);

                m_Events.Add(auditEvent);
            }
        }

        public NexAuditQueryResult Query(
            string resource,
            string actor,
            string action,
            int limit,
            int offset)
        {
            int safeLimit = Math.Max(1, Math.Min(limit, 100));
            int safeOffset = Math.Max(0, Math.Min(offset, 10000));

            lock (m_Sync)
            {
                IEnumerable<NexAuditEvent> query =
                    m_Events.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(resource))
                {
                    query = query.Where(x =>
                        string.Equals(
                            x.Resource,
                            resource.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(actor))
                {
                    query = query.Where(x =>
                        string.Equals(
                            x.Actor,
                            actor.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(action))
                {
                    string normalized = action.Trim();

                    if (normalized.EndsWith("*", StringComparison.Ordinal))
                    {
                        string prefix = normalized.Substring(
                            0,
                            normalized.Length - 1);

                        query = query.Where(x =>
                            x.Action.StartsWith(
                                prefix,
                                StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        query = query.Where(x =>
                            string.Equals(
                                x.Action,
                                normalized,
                                StringComparison.OrdinalIgnoreCase));
                    }
                }

                NexAuditEvent[] page = query
                    .OrderByDescending(x => x.Timestamp)
                    .ThenByDescending(x => x.EventId)
                    .Skip(safeOffset)
                    .Take(safeLimit + 1)
                    .ToArray();

                bool hasMore = page.Length > safeLimit;
                if (hasMore)
                    Array.Resize(ref page, safeLimit);

                return new NexAuditQueryResult(page, hasMore);
            }
        }

        private void Load()
        {
            if (!File.Exists(m_Path))
                return;

            int lineNumber = 0;
            foreach (string line in File.ReadLines(m_Path, Encoding.UTF8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    AuditRecord record =
                        JsonSerializer.Deserialize<AuditRecord>(line, s_Json);

                    NexAuditEvent auditEvent = record?.ToEvent();
                    if (auditEvent != null)
                        m_Events.Add(auditEvent);
                }
                catch (Exception e)
                {
                    throw new InvalidDataException(
                        "Invalid NexVerse audit record at line " +
                        lineNumber + " in " + m_Path + ".",
                        e);
                }
            }
        }

        private sealed class AuditRecord
        {
            public Guid EventId { get; set; }
            public DateTimeOffset Timestamp { get; set; }
            public string Actor { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
            public string Resource { get; set; } = string.Empty;
            public string CorrelationId { get; set; } = string.Empty;
            public Dictionary<string, string> Details { get; set; } =
                new Dictionary<string, string>();

            public static AuditRecord FromEvent(NexAuditEvent value)
            {
                return new AuditRecord
                {
                    EventId = value.EventId,
                    Timestamp = value.Timestamp,
                    Actor = value.Actor,
                    Action = value.Action,
                    Resource = value.Resource,
                    CorrelationId = value.CorrelationId,
                    Details = new Dictionary<string, string>(
                        value.Details ?? new Dictionary<string, string>())
                };
            }

            public NexAuditEvent ToEvent()
            {
                if (EventId == Guid.Empty)
                    return null;

                return new NexAuditEvent(
                    EventId,
                    Timestamp,
                    Actor,
                    Action,
                    Resource,
                    CorrelationId,
                    Details ?? new Dictionary<string, string>());
            }
        }
    }
}
