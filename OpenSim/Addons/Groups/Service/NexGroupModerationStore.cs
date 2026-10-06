// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenMetaverse;

namespace OpenSim.Groups
{
    public sealed class NexGroupBanRecord
    {
        public string GroupID { get; set; } = string.Empty;
        public string AgentID { get; set; } = string.Empty;
        public string ActorID { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexGroupModerationSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public List<NexGroupBanRecord> Bans { get; set; } =
            new List<NexGroupBanRecord>();
    }

    public sealed class NexGroupModerationStore
    {
        public const int CurrentSchemaVersion = 1;

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private readonly JsonSerializerOptions m_Json =
            new JsonSerializerOptions { WriteIndented = true };
        private NexGroupModerationSnapshot m_State;

        public NexGroupModerationStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("NexGroups moderation store path is required.", nameof(path));

            m_Path = Path.GetFullPath(path);
            m_State = Load();
        }

        public bool IsBanned(UUID groupId, string agentId)
        {
            if (groupId.IsZero() || string.IsNullOrWhiteSpace(agentId))
                return false;

            lock (m_Sync)
            {
                return m_State.Bans.Any(x =>
                    string.Equals(
                        x.GroupID,
                        groupId.ToString(),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        x.AgentID,
                        agentId,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<NexGroupBanRecord> List(UUID groupId)
        {
            lock (m_Sync)
            {
                return m_State.Bans
                    .Where(x =>
                        string.Equals(
                            x.GroupID,
                            groupId.ToString(),
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexGroupBanRecord Add(
            UUID groupId,
            string agentId,
            string actorId,
            string reason)
        {
            if (groupId.IsZero())
                throw new ArgumentException("Group ID is required.", nameof(groupId));
            if (!UUID.TryParse(agentId, out UUID parsedAgent) || parsedAgent.IsZero())
                throw new ArgumentException("Agent ID is required.", nameof(agentId));

            lock (m_Sync)
            {
                NexGroupBanRecord existing =
                    m_State.Bans.FirstOrDefault(x =>
                        string.Equals(
                            x.GroupID,
                            groupId.ToString(),
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            x.AgentID,
                            parsedAgent.ToString(),
                            StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                    return Clone(existing);

                NexGroupBanRecord record =
                    new NexGroupBanRecord
                    {
                        GroupID = groupId.ToString(),
                        AgentID = parsedAgent.ToString(),
                        ActorID = actorId ?? string.Empty,
                        Reason = (reason ?? string.Empty).Trim(),
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                m_State.Bans.Add(record);
                Save();
                return Clone(record);
            }
        }

        public bool Remove(UUID groupId, string agentId)
        {
            lock (m_Sync)
            {
                int removed =
                    m_State.Bans.RemoveAll(x =>
                        string.Equals(
                        x.GroupID,
                        groupId.ToString(),
                        StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            x.AgentID,
                            agentId,
                            StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                    Save();

                return removed > 0;
            }
        }

        public void RemoveGroup(UUID groupId)
        {
            lock (m_Sync)
            {
                if (m_State.Bans.RemoveAll(x =>
                    string.Equals(
                        x.GroupID,
                        groupId.ToString(),
                        StringComparison.OrdinalIgnoreCase)) > 0)
                    Save();
            }
        }

        private NexGroupModerationSnapshot Load()
        {
            if (!File.Exists(m_Path))
                return new NexGroupModerationSnapshot();

            NexGroupModerationSnapshot state =
                JsonSerializer.Deserialize<NexGroupModerationSnapshot>(
                    File.ReadAllText(m_Path),
                    m_Json) ??
                new NexGroupModerationSnapshot();

            if (state.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported NexGroups moderation schema version.");

            state.Bans ??= new List<NexGroupBanRecord>();
            return state;
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = m_Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(m_State, m_Json));
            File.Move(temp, m_Path, true);
        }

        private static NexGroupBanRecord Clone(NexGroupBanRecord record) =>
            new NexGroupBanRecord
            {
                GroupID = record.GroupID,
                AgentID = record.AgentID,
                ActorID = record.ActorID,
                Reason = record.Reason,
                CreatedAt = record.CreatedAt
            };
    }
}
