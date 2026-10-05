// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NexVerse.Core.Messaging;

namespace NexVerse.Core.ControlPlane
{
    public sealed class NexNodeRegionSnapshot
    {
        public NexNodeRegionSnapshot(
            string regionId,
            string name,
            string serverUri,
            int sizeX,
            int sizeY,
            int agentCount,
            DateTimeOffset lastSeen)
        {
            RegionId = regionId ?? string.Empty;
            Name = name ?? string.Empty;
            ServerUri = serverUri ?? string.Empty;
            SizeX = sizeX;
            SizeY = sizeY;
            AgentCount = agentCount;
            LastSeen = lastSeen;
        }

        public string RegionId { get; }
        public string Name { get; }
        public string ServerUri { get; }
        public int SizeX { get; }
        public int SizeY { get; }
        public int AgentCount { get; }
        public DateTimeOffset LastSeen { get; }
    }

    public sealed class NexNodeSnapshot
    {
        public NexNodeSnapshot(
            string nodeId,
            string hostname,
            string serverVersion,
            long uptimeSeconds,
            int processId,
            long workingSetBytes,
            double cpuSeconds,
            long diskFreeBytes,
            long diskTotalBytes,
            int regionCount,
            int agentCount,
            bool managedRegionCommands,
            string migrationStorageId,
            bool maintenanceMode,
            bool draining,
            int lunaTextureDiagnosticCount,
            long lunaTextureOccurrenceCount,
            int lunaTextureRegionsAffected,
            DateTimeOffset? lunaTextureLastSeen,
            IReadOnlyDictionary<string, long> lunaTextureClassifications,
            string state,
            DateTimeOffset lastSeen,
            DateTimeOffset lastEventAt,
            IReadOnlyList<NexNodeRegionSnapshot> regions)
        {
            NodeId = nodeId ?? string.Empty;
            Hostname = hostname ?? string.Empty;
            ServerVersion = serverVersion ?? string.Empty;
            UptimeSeconds = uptimeSeconds;
            ProcessId = processId;
            WorkingSetBytes = workingSetBytes;
            CpuSeconds = cpuSeconds;
            DiskFreeBytes = diskFreeBytes;
            DiskTotalBytes = diskTotalBytes;
            RegionCount = regionCount;
            AgentCount = agentCount;
            ManagedRegionCommands = managedRegionCommands;
            MigrationStorageId = migrationStorageId ?? string.Empty;
            MaintenanceMode = maintenanceMode;
            Draining = draining;
            LunaTextureDiagnosticCount = Math.Max(0, lunaTextureDiagnosticCount);
            LunaTextureOccurrenceCount = Math.Max(0, lunaTextureOccurrenceCount);
            LunaTextureRegionsAffected = Math.Max(0, lunaTextureRegionsAffected);
            LunaTextureLastSeen = lunaTextureLastSeen;
            LunaTextureClassifications =
                lunaTextureClassifications ??
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);
            State = state ?? "unknown";
            LastSeen = lastSeen;
            LastEventAt = lastEventAt;
            Regions = regions ?? Array.Empty<NexNodeRegionSnapshot>();
        }

        public string NodeId { get; }
        public string Hostname { get; }
        public string ServerVersion { get; }
        public long UptimeSeconds { get; }
        public int ProcessId { get; }
        public long WorkingSetBytes { get; }
        public double CpuSeconds { get; }
        public long DiskFreeBytes { get; }
        public long DiskTotalBytes { get; }
        public int RegionCount { get; }
        public int AgentCount { get; }
        public bool ManagedRegionCommands { get; }
        public string MigrationStorageId { get; }
        public bool MaintenanceMode { get; }
        public bool Draining { get; }
        public int LunaTextureDiagnosticCount { get; }
        public long LunaTextureOccurrenceCount { get; }
        public int LunaTextureRegionsAffected { get; }
        public DateTimeOffset? LunaTextureLastSeen { get; }
        public IReadOnlyDictionary<string, long> LunaTextureClassifications { get; }
        public string State { get; }
        public DateTimeOffset LastSeen { get; }
        public DateTimeOffset LastEventAt { get; }
        public IReadOnlyList<NexNodeRegionSnapshot> Regions { get; }
    }

    /// <summary>
    /// Robust-side projection of simulator NodeAgent lifecycle and heartbeat events.
    /// State is intentionally in-memory: NodeAgents continuously rebuild it after
    /// Robust restarts and stale detection is based on the local receive clock.
    /// </summary>
    public sealed class NexNodeRegistry : IDisposable
    {
        private readonly ConcurrentDictionary<string, NodeRecord> m_Nodes =
            new ConcurrentDictionary<string, NodeRecord>(
                StringComparer.OrdinalIgnoreCase);

        private readonly IDisposable m_Subscription;
        private readonly TimeSpan m_StaleAfter;
        private int m_Disposed;

        public int StaleAfterSeconds =>
            (int)m_StaleAfter.TotalSeconds;

        public NexNodeRegistry(
            INexEventBus eventBus,
            int staleAfterSeconds = 90)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            m_StaleAfter =
                TimeSpan.FromSeconds(
                    Math.Max(10, staleAfterSeconds));

            m_Subscription =
                eventBus.Subscribe(
                    "*",
                    HandleEvent);
        }

        public IReadOnlyList<NexNodeSnapshot> List()
        {
            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            return m_Nodes.Values
                .Select(record =>
                    Snapshot(record, now))
                .OrderBy(
                    snapshot => snapshot.NodeId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public NexNodeSnapshot Get(
            string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) ||
                !m_Nodes.TryGetValue(
                    nodeId.Trim(),
                    out NodeRecord record))
            {
                return null;
            }

            return Snapshot(
                record,
                DateTimeOffset.UtcNow);
        }

        public NexNodeSnapshot FindNodeForRegion(
            string regionId)
        {
            if (string.IsNullOrWhiteSpace(regionId))
                return null;

            string normalized =
                regionId.Trim();

            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            return m_Nodes.Values
                .Where(record =>
                {
                    lock (record.Sync)
                    {
                        return record.Regions.ContainsKey(
                            normalized);
                    }
                })
                .Select(record =>
                    Snapshot(record, now))
                .OrderBy(snapshot =>
                    StateOrder(snapshot.State))
                .ThenByDescending(snapshot =>
                    snapshot.LastSeen)
                .FirstOrDefault();
        }

        private void HandleEvent(
            NexEvent nexEvent)
        {
            if (nexEvent == null)
                return;

            switch (nexEvent.Name?.Trim().ToLowerInvariant())
            {
                case "node.online":
                case "node.heartbeat":
                    ApplyNodeStatus(
                        nexEvent,
                        false);
                    break;

                case "node.offline":
                    ApplyNodeStatus(
                        nexEvent,
                        true);
                    break;

                case "region.online":
                    ApplyRegionOnline(
                        nexEvent);
                    break;

                case "region.offline":
                    ApplyRegionOffline(
                        nexEvent);
                    break;
            }
        }

        private void ApplyNodeStatus(
            NexEvent nexEvent,
            bool explicitOffline)
        {
            if (!TryData(
                    nexEvent,
                    "node_id",
                    out string nodeId))
            {
                return;
            }

            DateTimeOffset receivedAt =
                DateTimeOffset.UtcNow;

            NodeRecord record =
                m_Nodes.GetOrAdd(
                    nodeId,
                    id => new NodeRecord(id));

            lock (record.Sync)
            {
                record.ExplicitOffline =
                    explicitOffline;
                record.LastSeen =
                    receivedAt;
                record.LastEventAt =
                    nexEvent.Timestamp;

                AssignString(
                    nexEvent,
                    "hostname",
                    value => record.Hostname = value);
                AssignString(
                    nexEvent,
                    "server_version",
                    value => record.ServerVersion = value);
                AssignLong(
                    nexEvent,
                    "uptime_seconds",
                    value => record.UptimeSeconds = value);
                AssignInt(
                    nexEvent,
                    "process_id",
                    value => record.ProcessId = value);
                AssignLong(
                    nexEvent,
                    "working_set_bytes",
                    value => record.WorkingSetBytes = value);
                AssignDouble(
                    nexEvent,
                    "cpu_seconds",
                    value => record.CpuSeconds = value);
                AssignLong(nexEvent, "disk_free_bytes", value => record.DiskFreeBytes = value);
                AssignLong(nexEvent, "disk_total_bytes", value => record.DiskTotalBytes = value);
                AssignInt(
                    nexEvent,
                    "region_count",
                    value => record.RegionCount = value);
                AssignInt(
                    nexEvent,
                    "agent_count",
                    value => record.AgentCount = value);
                AssignBool(nexEvent, "maintenance_mode", value => record.MaintenanceMode = value);
                AssignBool(nexEvent, "draining", value => record.Draining = value);
                AssignBool(
                    nexEvent,
                    "managed_region_commands",
                    value => record.ManagedRegionCommands = value);
                AssignString(
                    nexEvent,
                    "migration_storage_id",
                    value => record.MigrationStorageId = value);
                AssignInt(
                    nexEvent,
                    "luna_texture_diagnostic_count",
                    value => record.LunaTextureDiagnosticCount = Math.Max(0, value));
                AssignLong(
                    nexEvent,
                    "luna_texture_occurrence_count",
                    value => record.LunaTextureOccurrenceCount = Math.Max(0, value));
                AssignInt(
                    nexEvent,
                    "luna_texture_regions_affected",
                    value => record.LunaTextureRegionsAffected = Math.Max(0, value));
                AssignNullableDateTimeOffset(
                    nexEvent,
                    "luna_texture_last_seen_utc",
                    value => record.LunaTextureLastSeen = value);
                AssignLongDictionary(
                    nexEvent,
                    "luna_texture_classifications_json",
                    value => record.LunaTextureClassifications = value);

                if (TryData(
                        nexEvent,
                        "regions",
                        out string regionsRaw,
                        allowEmpty: true))
                {
                    SynchronizeRegions(
                        record,
                        regionsRaw,
                        receivedAt);
                }
            }
        }

        private void ApplyRegionOnline(
            NexEvent nexEvent)
        {
            if (!TryData(
                    nexEvent,
                    "node_id",
                    out string nodeId) ||
                !TryData(
                    nexEvent,
                    "region_id",
                    out string regionId))
            {
                return;
            }

            DateTimeOffset receivedAt =
                DateTimeOffset.UtcNow;

            NodeRecord record =
                m_Nodes.GetOrAdd(
                    nodeId,
                    id => new NodeRecord(id));

            lock (record.Sync)
            {
                record.ExplicitOffline = false;
                record.LastSeen = receivedAt;
                record.LastEventAt = nexEvent.Timestamp;

                record.Regions.TryGetValue(
                    regionId,
                    out RegionRecord region);

                region ??=
                    new RegionRecord(regionId);

                if (TryData(
                        nexEvent,
                        "region_name",
                        out string name,
                        allowEmpty: true))
                {
                    region.Name = name;
                }

                if (TryData(
                        nexEvent,
                        "server_uri",
                        out string serverUri,
                        allowEmpty: true))
                {
                    region.ServerUri = serverUri;
                }

                if (TryParseInt(
                        nexEvent,
                        "size_x",
                        out int sizeX))
                {
                    region.SizeX = sizeX;
                }

                if (TryParseInt(
                        nexEvent,
                        "size_y",
                        out int sizeY))
                {
                    region.SizeY = sizeY;
                }

                if (TryParseInt(
                        nexEvent,
                        "agent_count",
                        out int agentCount))
                {
                    region.AgentCount = agentCount;
                }

                region.LastSeen =
                    receivedAt;

                record.Regions[regionId] =
                    region;
                record.RegionCount =
                    record.Regions.Count;

                record.AgentCount =
                    Math.Max(
                        record.AgentCount,
                        record.Regions.Values.Sum(x =>
                            Math.Max(0, x.AgentCount)));
            }
        }

        private void ApplyRegionOffline(
            NexEvent nexEvent)
        {
            if (!TryData(
                    nexEvent,
                    "node_id",
                    out string nodeId) ||
                !TryData(
                    nexEvent,
                    "region_id",
                    out string regionId))
            {
                return;
            }

            DateTimeOffset receivedAt =
                DateTimeOffset.UtcNow;

            NodeRecord record =
                m_Nodes.GetOrAdd(
                    nodeId,
                    id => new NodeRecord(id));

            lock (record.Sync)
            {
                record.ExplicitOffline = false;
                record.LastSeen = receivedAt;
                record.LastEventAt = nexEvent.Timestamp;
                record.Regions.Remove(regionId);
                record.RegionCount =
                    record.Regions.Count;
                record.AgentCount =
                    record.Regions.Values.Sum(x =>
                        Math.Max(0, x.AgentCount));
            }
        }

        private static void SynchronizeRegions(
            NodeRecord record,
            string raw,
            DateTimeOffset receivedAt)
        {
            Dictionary<string, string> current =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (string entry in raw.Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator =
                        entry.IndexOf('|');

                    string regionId =
                        separator >= 0
                            ? entry.Substring(0, separator)
                            : entry;

                    string name =
                        separator >= 0
                            ? entry.Substring(separator + 1)
                            : string.Empty;

                    regionId =
                        regionId.Trim();

                    if (regionId.Length == 0)
                        continue;

                    current[regionId] =
                        name.Trim();
                }
            }

            foreach (string existingId in
                     record.Regions.Keys.ToArray())
            {
                if (!current.ContainsKey(existingId))
                    record.Regions.Remove(existingId);
            }

            foreach (KeyValuePair<string, string> item in current)
            {
                if (!record.Regions.TryGetValue(
                        item.Key,
                        out RegionRecord region))
                {
                    region =
                        new RegionRecord(item.Key);
                }

                if (!string.IsNullOrWhiteSpace(item.Value))
                    region.Name = item.Value;

                region.LastSeen =
                    receivedAt;

                record.Regions[item.Key] =
                    region;
            }

            record.RegionCount =
                record.Regions.Count;
        }

        private NexNodeSnapshot Snapshot(
            NodeRecord record,
            DateTimeOffset now)
        {
            lock (record.Sync)
            {
                string state =
                    record.ExplicitOffline
                        ? "offline"
                        : now - record.LastSeen >
                          m_StaleAfter
                            ? "stale"
                            : "online";

                NexNodeRegionSnapshot[] regions =
                    record.Regions.Values
                        .OrderBy(
                            region => region.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            region => region.RegionId,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(region =>
                            new NexNodeRegionSnapshot(
                                region.RegionId,
                                region.Name,
                                region.ServerUri,
                                region.SizeX,
                                region.SizeY,
                                region.AgentCount,
                                region.LastSeen))
                        .ToArray();

                return new NexNodeSnapshot(
                    record.NodeId,
                    record.Hostname,
                    record.ServerVersion,
                    record.UptimeSeconds,
                    record.ProcessId,
                    record.WorkingSetBytes,
                    record.CpuSeconds,
                    record.DiskFreeBytes,
                    record.DiskTotalBytes,
                    record.RegionCount,
                    record.AgentCount,
                    record.ManagedRegionCommands,
                    record.MigrationStorageId,
                    record.MaintenanceMode,
                    record.Draining,
                    record.LunaTextureDiagnosticCount,
                    record.LunaTextureOccurrenceCount,
                    record.LunaTextureRegionsAffected,
                    record.LunaTextureLastSeen,
                    new Dictionary<string, long>(
                        record.LunaTextureClassifications,
                        StringComparer.OrdinalIgnoreCase),
                    state,
                    record.LastSeen,
                    record.LastEventAt,
                    regions);
            }
        }

        private static int StateOrder(
            string state)
        {
            return state switch
            {
                "online" => 0,
                "stale" => 1,
                "offline" => 2,
                _ => 3
            };
        }

        private static bool TryData(
            NexEvent nexEvent,
            string key,
            out string value,
            bool allowEmpty = false)
        {
            value = string.Empty;

            if (nexEvent?.Data == null ||
                !nexEvent.Data.TryGetValue(
                    key,
                    out string raw) ||
                raw == null)
            {
                return false;
            }

            value =
                raw.Trim();

            return allowEmpty ||
                   value.Length > 0;
        }

        private static bool TryParseInt(
            NexEvent nexEvent,
            string key,
            out int value)
        {
            value = 0;

            return
                TryData(
                    nexEvent,
                    key,
                    out string raw) &&
                int.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static bool TryParseLong(
            NexEvent nexEvent,
            string key,
            out long value)
        {
            value = 0;

            return
                TryData(
                    nexEvent,
                    key,
                    out string raw) &&
                long.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static bool TryParseDouble(
            NexEvent nexEvent,
            string key,
            out double value)
        {
            value = 0;

            return
                TryData(
                    nexEvent,
                    key,
                    out string raw) &&
                double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static void AssignString(
            NexEvent nexEvent,
            string key,
            Action<string> assign)
        {
            if (TryData(
                    nexEvent,
                    key,
                    out string value,
                    allowEmpty: true))
            {
                assign(value);
            }
        }

        private static void AssignInt(
            NexEvent nexEvent,
            string key,
            Action<int> assign)
        {
            if (TryParseInt(
                    nexEvent,
                    key,
                    out int value))
            {
                assign(value);
            }
        }

        private static void AssignLong(
            NexEvent nexEvent,
            string key,
            Action<long> assign)
        {
            if (TryParseLong(
                    nexEvent,
                    key,
                    out long value))
            {
                assign(value);
            }
        }

        private static void AssignBool(
            NexEvent nexEvent,
            string key,
            Action<bool> assign)
        {
            if (TryData(
                    nexEvent,
                    key,
                    out string raw) &&
                bool.TryParse(
                    raw,
                    out bool value))
            {
                assign(value);
            }
        }

        private static void AssignDouble(
            NexEvent nexEvent,
            string key,
            Action<double> assign)
        {
            if (TryParseDouble(
                    nexEvent,
                    key,
                    out double value))
            {
                assign(value);
            }
        }

        private static void AssignNullableDateTimeOffset(
            NexEvent nexEvent,
            string key,
            Action<DateTimeOffset?> assign)
        {
            if (!TryData(
                    nexEvent,
                    key,
                    out string raw,
                    allowEmpty: true))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                assign(null);
                return;
            }

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset value))
            {
                assign(value);
            }
        }

        private static void AssignLongDictionary(
            NexEvent nexEvent,
            string key,
            Action<Dictionary<string, long>> assign)
        {
            if (!TryData(
                    nexEvent,
                    key,
                    out string raw,
                    allowEmpty: true))
            {
                return;
            }

            Dictionary<string, long> parsed =
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    Dictionary<string, long> values =
                        JsonSerializer.Deserialize<Dictionary<string, long>>(raw);

                    if (values != null)
                    {
                        foreach (KeyValuePair<string, long> item in values)
                        {
                            string name =
                                item.Key?.Trim().ToLowerInvariant();

                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            parsed[name] =
                                Math.Max(0, item.Value);
                        }
                    }
                }
                catch (JsonException)
                {
                    return;
                }
            }

            assign(parsed);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(
                    ref m_Disposed,
                    1) != 0)
            {
                return;
            }

            m_Subscription?.Dispose();
            m_Nodes.Clear();
        }

        private sealed class NodeRecord
        {
            public NodeRecord(
                string nodeId)
            {
                NodeId = nodeId;
                LastSeen = DateTimeOffset.UtcNow;
                LastEventAt = LastSeen;
            }

            public object Sync { get; } =
                new object();

            public string NodeId { get; }
            public string Hostname = string.Empty;
            public string ServerVersion = string.Empty;
            public long UptimeSeconds;
            public int ProcessId;
            public long WorkingSetBytes;
            public double CpuSeconds;
            public long DiskFreeBytes;
            public long DiskTotalBytes;
            public int RegionCount;
            public int AgentCount;
            public bool ManagedRegionCommands;
            public string MigrationStorageId = string.Empty;
            public bool MaintenanceMode;
            public bool Draining;
            public int LunaTextureDiagnosticCount;
            public long LunaTextureOccurrenceCount;
            public int LunaTextureRegionsAffected;
            public DateTimeOffset? LunaTextureLastSeen;
            public Dictionary<string, long> LunaTextureClassifications =
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);
            public bool ExplicitOffline;
            public DateTimeOffset LastSeen;
            public DateTimeOffset LastEventAt;

            public Dictionary<string, RegionRecord> Regions { get; } =
                new Dictionary<string, RegionRecord>(
                    StringComparer.OrdinalIgnoreCase);
        }

        private sealed class RegionRecord
        {
            public RegionRecord(
                string regionId)
            {
                RegionId = regionId;
                LastSeen = DateTimeOffset.UtcNow;
            }

            public string RegionId { get; }
            public string Name = string.Empty;
            public string ServerUri = string.Empty;
            public int SizeX;
            public int SizeY;
            public int AgentCount;
            public DateTimeOffset LastSeen;
        }
    }
}
