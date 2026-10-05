// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using NexVerse.Core.Messaging;
using NexVerse.RegionModules.Archives;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace NexVerse.RegionModules.NodeAgent
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "NexVerseNodeAgentModule")]
    public sealed class NexVerseNodeAgentModule :
        ISharedRegionModule,
        INexVerseEventBusModule,
        ICrossRegionObjectMessageRouter
    {
        private const int DebugChannel = 0x7fffffff;
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexVerseNodeAgentModule));

        private readonly ConcurrentDictionary<UUID, Scene> m_Scenes =
            new ConcurrentDictionary<UUID, Scene>();
        private readonly ConcurrentDictionary<UUID, ObjectMessageRateState> m_ObjectMessageOutboundRates =
            new ConcurrentDictionary<UUID, ObjectMessageRateState>();
        private readonly ConcurrentDictionary<UUID, ObjectMessageRateState> m_ObjectMessageInboundRates =
            new ConcurrentDictionary<UUID, ObjectMessageRateState>();

        private bool m_Enabled;
        private string m_NodeId = string.Empty;
        private string m_SharedKey = string.Empty;
        private string m_InboundPath = "/internal/nexbus/v1/events";
        private int m_HeartbeatSeconds = 30;
        private bool m_ObjectMessagingEnabled;
        private bool m_AllowCrossOwnerObjectMessages;
        private int m_ObjectMessagesPerSecond = 20;
        private int m_ObjectMessageMaxAgeSeconds = 30;
        private string m_MigrationStorageId = string.Empty;
        private SimulatorNexEventTransport m_Transport;
        private DistributedNexEventBus m_Bus;
        private Timer m_HeartbeatTimer;
        private IDisposable m_CreateRegionSubscription;
        private IDisposable m_MoveRegionSubscription;
        private IDisposable m_RegionLifecycleSubscription;
        private IDisposable m_NodeControlSubscription;
        private IDisposable m_OarControlSubscription;
        private IDisposable m_IarControlSubscription;
        private IDisposable m_ObjectMessageSubscription;
        private volatile bool m_MaintenanceMode;
        private volatile bool m_Draining;
        private DateTimeOffset m_StartedAt;

        public string NodeId => m_NodeId;
        public bool Enabled => m_Enabled && m_ObjectMessagingEnabled;
        public string Name => "NexVerseNodeAgentModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["NexVerseNodeAgent"];
            if (config == null || !config.GetBoolean("Enabled", false))
                return;

            string peerUrl = config.GetString("PeerUrl", string.Empty).Trim();
            string sharedKey = config.GetString("SharedKey", string.Empty);

            if (!Uri.TryCreate(peerUrl, UriKind.Absolute, out Uri peer))
            {
                m_Log.Error("[NEX-NODE]: NexVerseNodeAgent PeerUrl is missing or invalid; module disabled.");
                return;
            }

            try
            {
                NexBusProtocol.ValidateSharedKey(sharedKey);
            }
            catch (Exception e)
            {
                m_Log.Error("[NEX-NODE]: NexVerseNodeAgent SharedKey is invalid; module disabled.", e);
                return;
            }

            m_NodeId = config.GetString("NodeId", Environment.MachineName).Trim();
            if (string.IsNullOrWhiteSpace(m_NodeId))
                m_NodeId = Environment.MachineName;

            m_SharedKey = sharedKey;
            m_InboundPath = config.GetString(
                "InboundPath",
                "/internal/nexbus/v1/events").Trim();
            m_HeartbeatSeconds = Math.Max(5, config.GetInt("HeartbeatSeconds", 30));
            m_ObjectMessagingEnabled =
                config.GetBoolean(
                    "CrossRegionObjectMessaging",
                    false);
            m_AllowCrossOwnerObjectMessages =
                config.GetBoolean(
                    "AllowCrossOwnerObjectMessages",
                    false);
            m_ObjectMessagesPerSecond =
                Math.Clamp(
                    config.GetInt(
                        "ObjectMessagesPerSecond",
                        20),
                    1,
                    200);
            m_ObjectMessageMaxAgeSeconds =
                Math.Clamp(
                    config.GetInt(
                        "ObjectMessageMaxAgeSeconds",
                        30),
                    5,
                    300);

            IConfig oarConfig = source.Configs["OpenGenesisLINKOAR"];
            m_MigrationStorageId =
                (oarConfig?.GetString("MigrationStorageId", string.Empty) ?? string.Empty)
                    .Trim();

            int queueCapacity = Math.Max(128, config.GetInt("QueueCapacity", 2048));
            int timeoutMs = Math.Max(250, config.GetInt("RequestTimeoutMilliseconds", 2000));
            int deduplicationWindow = Math.Max(128, config.GetInt("DeduplicationWindow", 10000));

            m_Transport = new SimulatorNexEventTransport(
                m_NodeId,
                peer,
                m_SharedKey,
                queueCapacity,
                timeoutMs);
            m_Bus = new DistributedNexEventBus(m_Transport, deduplicationWindow);
            m_StartedAt = DateTimeOffset.UtcNow;
            m_Enabled = true;

            m_Log.InfoFormat(
                "[NEX-NODE]: NodeAgent enabled for node {0}; NexBus peer {1}",
                m_NodeId,
                peer);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            m_Scenes[scene.RegionInfo.RegionID] = scene;
            scene.RegisterModuleInterface<INexVerseEventBusModule>(this);
            scene.RegisterModuleInterface<ICrossRegionObjectMessageRouter>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            PublishRegionEvent("region.online", scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            PublishRegionEvent("region.offline", scene);
            scene.UnregisterModuleInterface<ICrossRegionObjectMessageRouter>(this);
            scene.UnregisterModuleInterface<INexVerseEventBusModule>(this);
            m_Scenes.TryRemove(scene.RegionInfo.RegionID, out _);
        }

        public void PostInitialise()
        {
            if (!m_Enabled)
                return;

            if (MainServer.Instance == null)
            {
                m_Log.Error("[NEX-NODE]: MainServer is unavailable; NodeAgent inbound NexBus disabled.");
                return;
            }

            MainServer.Instance.AddSimpleStreamHandler(
                new SimpleStreamHandler(
                    m_InboundPath,
                    HandleInbound,
                    "NexVerse Simulator NexBus"));

            m_CreateRegionSubscription =
                m_Bus.Subscribe(
                    "region.control.create.requested",
                    HandleCreateRegionCommand);
            m_MoveRegionSubscription =
                m_Bus.Subscribe(
                    "region.control.move.requested",
                    HandleMoveRegionCommand);
            m_RegionLifecycleSubscription =
                m_Bus.Subscribe(
                    "region.control.lifecycle.requested",
                    HandleRegionLifecycleCommand);
            m_NodeControlSubscription = m_Bus.Subscribe("node.control.requested", HandleNodeControlCommand);
            m_OarControlSubscription = m_Bus.Subscribe("archive.oar.requested", HandleOarCommand);
            m_IarControlSubscription = m_Bus.Subscribe("archive.iar.requested", HandleIarCommand);
            if (m_ObjectMessagingEnabled)
            {
                m_ObjectMessageSubscription =
                    m_Bus.Subscribe(
                        "object.message.requested",
                        HandleObjectMessage);
            }

            m_HeartbeatTimer = new Timer(
                _ => PublishHeartbeatSafe(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(m_HeartbeatSeconds));

            Publish(new NexEvent(
                "node.online",
                "nexverse.simulator",
                NodeData()));
        }

        public void Close()
        {
            if (!m_Enabled)
                return;

            try
            {
                Publish(new NexEvent(
                    "node.offline",
                    "nexverse.simulator",
                    NodeData()));
            }
            catch
            {
            }

            m_CreateRegionSubscription?.Dispose();
            m_CreateRegionSubscription = null;
            m_MoveRegionSubscription?.Dispose();
            m_MoveRegionSubscription = null;
            m_RegionLifecycleSubscription?.Dispose();
            m_RegionLifecycleSubscription = null;
            m_NodeControlSubscription?.Dispose();
            m_NodeControlSubscription = null;
            m_OarControlSubscription?.Dispose();
            m_OarControlSubscription = null;
            m_IarControlSubscription?.Dispose();
            m_IarControlSubscription = null;
            m_ObjectMessageSubscription?.Dispose();
            m_ObjectMessageSubscription = null;
            m_ObjectMessageOutboundRates.Clear();
            m_ObjectMessageInboundRates.Clear();
            m_HeartbeatTimer?.Dispose();
            m_Bus?.Dispose();
            m_Transport = null;
            m_Bus = null;
            m_Enabled = false;
        }

        public IDisposable Subscribe(
            string eventName,
            Action<NexEvent> handler)
        {
            if (!m_Enabled || m_Bus == null)
                throw new InvalidOperationException("NexVerse NodeAgent is not enabled.");

            return m_Bus.Subscribe(eventName, handler);
        }

        public void Publish(NexEvent nexEvent)
        {
            if (!m_Enabled || m_Bus == null)
                return;

            m_Bus.Publish(nexEvent);
        }

        public bool TryRoute(
            UUID sourceRegionId,
            UUID sourceObjectId,
            UUID sourceOwnerId,
            string sourceName,
            Vector3 sourcePosition,
            UUID targetObjectId,
            int channel,
            string message)
        {
            if (!Enabled ||
                m_Bus == null ||
                sourceRegionId.IsZero() ||
                sourceObjectId.IsZero() ||
                sourceOwnerId.IsZero() ||
                targetObjectId.IsZero() ||
                channel == DebugChannel)
            {
                return false;
            }

            if (!m_Scenes.TryGetValue(
                    sourceRegionId,
                    out Scene sourceScene))
            {
                return false;
            }

            SceneObjectPart sourcePart =
                sourceScene.GetSceneObjectPart(
                    sourceObjectId);
            if (sourcePart == null ||
                sourcePart.IsDeleted ||
                sourcePart.OwnerID != sourceOwnerId)
            {
                return false;
            }

            string payload =
                message ?? string.Empty;
            if (payload.Length > 1023)
                payload = payload[..1023];

            if (!TryTakeObjectMessageRateSlot(
                    m_ObjectMessageOutboundRates,
                    sourceObjectId))
            {
                m_Log.WarnFormat(
                    "[NEX-OBJECT]: Rate limit reached for source object {0} in region {1}.",
                    sourceObjectId,
                    sourceScene.RegionInfo.RegionName);
                return false;
            }

            Dictionary<string, string> data =
                new Dictionary<string, string>
                {
                    ["source_node_id"] =
                        m_NodeId,
                    ["source_region_id"] =
                        sourceRegionId.ToString(),
                    ["source_object_id"] =
                        sourceObjectId.ToString(),
                    ["source_owner_id"] =
                        sourceOwnerId.ToString(),
                    ["source_name"] =
                        string.IsNullOrWhiteSpace(sourceName)
                            ? sourcePart.Name ?? string.Empty
                            : sourceName.Trim(),
                    ["source_position"] =
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0:R},{1:R},{2:R}",
                            sourcePosition.X,
                            sourcePosition.Y,
                            sourcePosition.Z),
                    ["target_object_id"] =
                        targetObjectId.ToString(),
                    ["channel"] =
                        channel.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ["message"] =
                        payload
                };

            Publish(new NexEvent(
                "object.message.requested",
                "nexverse.simulator",
                data));

            return true;
        }

        private void HandleObjectMessage(
            NexEvent nexEvent)
        {
            if (!Enabled ||
                nexEvent == null ||
                nexEvent.Data == null)
            {
                return;
            }

            TimeSpan age =
                DateTimeOffset.UtcNow -
                nexEvent.Timestamp;
            if (age >
                    TimeSpan.FromSeconds(
                        m_ObjectMessageMaxAgeSeconds) ||
                age <
                    TimeSpan.FromSeconds(-5))
            {
                return;
            }

            if (!TryCommandData(
                    nexEvent,
                    "source_object_id",
                    out string rawSourceObject) ||
                !UUID.TryParse(
                    rawSourceObject,
                    out UUID sourceObjectId) ||
                sourceObjectId.IsZero() ||
                !TryCommandData(
                    nexEvent,
                    "source_owner_id",
                    out string rawSourceOwner) ||
                !UUID.TryParse(
                    rawSourceOwner,
                    out UUID sourceOwnerId) ||
                sourceOwnerId.IsZero() ||
                !TryCommandData(
                    nexEvent,
                    "target_object_id",
                    out string rawTarget) ||
                !UUID.TryParse(
                    rawTarget,
                    out UUID targetObjectId) ||
                targetObjectId.IsZero() ||
                !TryCommandInt(
                    nexEvent,
                    "channel",
                    out int channel) ||
                channel ==
                    DebugChannel)
            {
                return;
            }

            string sourceName =
                nexEvent.Data.TryGetValue(
                    "source_name",
                    out string rawName)
                    ? rawName ?? string.Empty
                    : string.Empty;
            string message =
                nexEvent.Data.TryGetValue(
                    "message",
                    out string rawMessage)
                    ? rawMessage ?? string.Empty
                    : string.Empty;
            if (message.Length > 1023)
                message = message[..1023];

            foreach (Scene scene in
                     m_Scenes.Values)
            {
                SceneObjectPart targetPart =
                    scene.GetSceneObjectPart(
                        targetObjectId);
                if (targetPart == null ||
                    targetPart.IsDeleted)
                {
                    continue;
                }

                if (!m_AllowCrossOwnerObjectMessages &&
                    targetPart.OwnerID !=
                        sourceOwnerId)
                {
                    m_Log.DebugFormat(
                        "[NEX-OBJECT]: Cross-owner object message rejected from {0} to {1}.",
                        sourceObjectId,
                        targetObjectId);
                    return;
                }

                if (!TryTakeObjectMessageRateSlot(
                        m_ObjectMessageInboundRates,
                        sourceObjectId))
                {
                    m_Log.WarnFormat(
                        "[NEX-OBJECT]: Inbound rate limit reached for source object {0}.",
                        sourceObjectId);
                    return;
                }

                IWorldComm worldComm =
                    scene.RequestModuleInterface<IWorldComm>();
                if (worldComm == null)
                    return;

                worldComm.DeliverMessageTo(
                    targetObjectId,
                    channel,
                    Vector3.Zero,
                    sourceName,
                    sourceObjectId,
                    message);

                Publish(new NexEvent(
                    "object.message.received",
                    "nexverse.simulator",
                    new Dictionary<string, string>
                    {
                        ["source_object_id"] =
                            sourceObjectId.ToString(),
                        ["source_owner_id"] =
                            sourceOwnerId.ToString(),
                        ["target_object_id"] =
                            targetObjectId.ToString(),
                        ["target_region_id"] =
                            scene.RegionInfo.RegionID.ToString(),
                        ["target_node_id"] =
                            m_NodeId,
                        ["channel"] =
                            channel.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        ["request_event_id"] =
                            nexEvent.EventId.ToString()
                    },
                    nexEvent.CorrelationId));

                return;
            }
        }

        private bool TryTakeObjectMessageRateSlot(
            ConcurrentDictionary<UUID, ObjectMessageRateState> rates,
            UUID sourceObjectId)
        {
            long currentSecond =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ObjectMessageRateState state =
                rates.GetOrAdd(
                    sourceObjectId,
                    _ => new ObjectMessageRateState());

            lock (state.SyncRoot)
            {
                if (state.WindowSecond !=
                    currentSecond)
                {
                    state.WindowSecond =
                        currentSecond;
                    state.Count = 0;
                }

                state.LastSeenSecond =
                    currentSecond;

                if (state.Count >=
                    m_ObjectMessagesPerSecond)
                {
                    return false;
                }

                state.Count++;
            }

            if (rates.Count > 4096)
                PruneObjectMessageRateStates(
                    rates,
                    currentSecond);

            return true;
        }

        private static void PruneObjectMessageRateStates(
            ConcurrentDictionary<UUID, ObjectMessageRateState> rates,
            long currentSecond)
        {
            foreach (KeyValuePair<UUID, ObjectMessageRateState> item in rates)
            {
                if (currentSecond -
                        item.Value.LastSeenSecond >
                    60)
                {
                    rates.TryRemove(
                        item.Key,
                        out _);
                }
            }
        }

        private void PublishHeartbeatSafe()
        {
            try
            {
                Publish(new NexEvent(
                    "node.heartbeat",
                    "nexverse.simulator",
                    NodeData()));
            }
            catch (Exception e)
            {
                m_Log.Warn("[NEX-NODE]: Failed to publish heartbeat.", e);
            }
        }

        private Dictionary<string, string> NodeData()
        {
            Scene[] scenes = m_Scenes.Values.ToArray();
            int agents = scenes.Sum(x => x.GetRootAgentCount());
            LunaTextureNodeSummary lunaTexture =
                CollectLunaTextureDiagnostics(scenes);

            using Process process = Process.GetCurrentProcess();
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "/");

            return new Dictionary<string, string>
            {
                ["node_id"] = m_NodeId,
                ["hostname"] = Environment.MachineName,
                ["server_version"] = OpenSim.VersionInfo.Version.Trim(),
                ["uptime_seconds"] = Math.Max(
                    0,
                    (long)(DateTimeOffset.UtcNow - m_StartedAt).TotalSeconds).ToString(),
                ["process_id"] = Environment.ProcessId.ToString(),
                ["working_set_bytes"] = process.WorkingSet64.ToString(),
                ["cpu_seconds"] = process.TotalProcessorTime.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                ["disk_free_bytes"] = drive.AvailableFreeSpace.ToString(),
                ["disk_total_bytes"] = drive.TotalSize.ToString(),
                ["region_count"] = scenes.Length.ToString(),
                ["agent_count"] = agents.ToString(),
                ["maintenance_mode"] = m_MaintenanceMode.ToString(),
                ["draining"] = m_Draining.ToString(),
                ["managed_region_commands"] =
                    (NexVerseManagedRegionHostPlugin.Current?.Enabled == true)
                        .ToString(),
                ["cross_region_object_messaging"] =
                    m_ObjectMessagingEnabled.ToString(),
                ["cross_owner_object_messaging"] =
                    m_AllowCrossOwnerObjectMessages.ToString(),
                ["object_messages_per_second"] =
                    m_ObjectMessagesPerSecond.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                ["object_message_max_age_seconds"] =
                    m_ObjectMessageMaxAgeSeconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                ["migration_storage_id"] =
                    m_MigrationStorageId,
                ["luna_texture_diagnostic_count"] =
                    lunaTexture.DiagnosticCount.ToString(),
                ["luna_texture_occurrence_count"] =
                    lunaTexture.OccurrenceCount.ToString(),
                ["luna_texture_regions_affected"] =
                    lunaTexture.RegionsAffected.ToString(),
                ["luna_texture_last_seen_utc"] =
                    lunaTexture.LastSeenUtc?.ToString("O") ?? string.Empty,
                ["luna_texture_classifications_json"] =
                    JsonSerializer.Serialize(lunaTexture.Classifications),
                ["regions"] = string.Join(
                    ";",
                    scenes
                        .OrderBy(x => x.RegionInfo.RegionName, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.RegionInfo.RegionID + "|" + x.RegionInfo.RegionName))
            };
        }

        private LunaTextureNodeSummary CollectLunaTextureDiagnostics(
            Scene[] scenes)
        {
            LunaTextureNodeSummary summary =
                new LunaTextureNodeSummary();

            foreach (Scene scene in scenes)
            {
                try
                {
                    ILunaTextureDiagnostics diagnostics =
                        scene.RequestModuleInterface<ILunaTextureDiagnostics>();

                    if (diagnostics == null)
                        continue;

                    IReadOnlyList<LunaTextureDiagnosticInfo> records =
                        diagnostics.GetDiagnostics();

                    if (records == null || records.Count == 0)
                        continue;

                    summary.RegionsAffected++;

                    foreach (LunaTextureDiagnosticInfo record in records)
                    {
                        if (record == null)
                            continue;

                        summary.DiagnosticCount++;
                        long occurrences =
                            Math.Max(1, record.Count);
                        summary.OccurrenceCount += occurrences;

                        string classification =
                            string.IsNullOrWhiteSpace(record.Classification)
                                ? "unknown"
                                : record.Classification.Trim().ToLowerInvariant();

                        if (!summary.Classifications.TryGetValue(
                                classification,
                                out long current))
                        {
                            current = 0;
                        }

                        summary.Classifications[classification] =
                            current + occurrences;

                        DateTime seen =
                            record.LastSeenUtc.Kind == DateTimeKind.Utc
                                ? record.LastSeenUtc
                                : record.LastSeenUtc.ToUniversalTime();

                        if (!summary.LastSeenUtc.HasValue ||
                            seen > summary.LastSeenUtc.Value)
                        {
                            summary.LastSeenUtc = seen;
                        }
                    }
                }
                catch (Exception e)
                {
                    m_Log.DebugFormat(
                        "[NEX-NODE]: LunaTexture diagnostics unavailable for region {0}: {1}",
                        scene?.RegionInfo.RegionName ?? "<unknown>",
                        e.Message);
                }
            }

            return summary;
        }

        private sealed class LunaTextureNodeSummary
        {
            public int DiagnosticCount;
            public long OccurrenceCount;
            public int RegionsAffected;
            public DateTime? LastSeenUtc;

            public Dictionary<string, long> Classifications { get; } =
                new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);
        }

        private void PublishRegionEvent(string name, Scene scene)
        {
            Publish(new NexEvent(
                name,
                "nexverse.simulator",
                new Dictionary<string, string>
                {
                    ["node_id"] = m_NodeId,
                    ["region_id"] = scene.RegionInfo.RegionID.ToString(),
                    ["region_name"] = scene.RegionInfo.RegionName,
                    ["server_uri"] = scene.RegionInfo.ServerURI ?? string.Empty,
                    ["size_x"] = scene.RegionInfo.RegionSizeX.ToString(),
                    ["size_y"] = scene.RegionInfo.RegionSizeY.ToString(),
                    ["agent_count"] = scene.GetRootAgentCount().ToString()
                }));
        }

        private void HandleCreateRegionCommand(
            NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(
                    nexEvent) ||
                !TryCommandData(
                    nexEvent,
                    "operation_id",
                    out string operationId))
            {
                return;
            }

            PublishOperationState(
                "region.control.operation.accepted",
                nexEvent,
                operationId,
                "create",
                "accepted");

            _ = Task.Run(() =>
            {
                try
                {
                    if (!TryCommandData(
                            nexEvent,
                            "region_id",
                            out string regionIdRaw) ||
                        !UUID.TryParse(
                            regionIdRaw,
                            out UUID regionId) ||
                        !TryCommandData(
                            nexEvent,
                            "region_name",
                            out string regionName) ||
                        !TryCommandInt(
                            nexEvent,
                            "grid_x",
                            out int gridX) ||
                        !TryCommandInt(
                            nexEvent,
                            "grid_y",
                            out int gridY) ||
                        !TryCommandInt(
                            nexEvent,
                            "size_x",
                            out int sizeX) ||
                        !TryCommandInt(
                            nexEvent,
                            "size_y",
                            out int sizeY) ||
                        !TryCommandInt(
                            nexEvent,
                            "estate_id",
                            out int estateId))
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "create",
                            "invalid_command_payload");
                        return;
                    }

                    NexVerseManagedRegionHostPlugin host =
                        NexVerseManagedRegionHostPlugin.Current;

                    if (host == null ||
                        !host.Enabled)
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "create",
                            "managed_region_commands_disabled");
                        return;
                    }

                    if (!host.TryCreateRegion(
                            regionId,
                            regionName,
                            gridX,
                            gridY,
                            sizeX,
                            sizeY,
                            estateId,
                            out string error,
                            out int internalPort,
                            out string _))
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "create",
                            error);
                        return;
                    }

                    PublishOperationState(
                        "region.control.operation.completed",
                        nexEvent,
                        operationId,
                        "create",
                        "region_created",
                        new Dictionary<string, string>
                        {
                            ["region_id"] =
                                regionId.ToString(),
                            ["region_name"] =
                                regionName,
                            ["grid_x"] =
                                gridX.ToString(),
                            ["grid_y"] =
                                gridY.ToString(),
                            ["size_x"] =
                                sizeX.ToString(),
                            ["size_y"] =
                                sizeY.ToString(),
                            ["internal_port"] =
                                internalPort.ToString()
                        });
                }
                catch (Exception e)
                {
                    m_Log.Error(
                        "[NEX-NODE]: Managed create-region command failed.",
                        e);

                    PublishOperationState(
                        "region.control.operation.failed",
                        nexEvent,
                        operationId,
                        "create",
                        "region_create_unhandled_failure");
                }
            });
        }

        private void HandleMoveRegionCommand(
            NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(
                    nexEvent) ||
                !TryCommandData(
                    nexEvent,
                    "operation_id",
                    out string operationId))
            {
                return;
            }

            PublishOperationState(
                "region.control.operation.accepted",
                nexEvent,
                operationId,
                "move",
                "accepted");

            _ = Task.Run(() =>
            {
                try
                {
                    if (!TryCommandData(
                            nexEvent,
                            "region_id",
                            out string regionIdRaw) ||
                        !UUID.TryParse(
                            regionIdRaw,
                            out UUID regionId) ||
                        !TryCommandInt(
                            nexEvent,
                            "grid_x",
                            out int gridX) ||
                        !TryCommandInt(
                            nexEvent,
                            "grid_y",
                            out int gridY))
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "move",
                            "invalid_command_payload");
                        return;
                    }

                    NexVerseManagedRegionHostPlugin host =
                        NexVerseManagedRegionHostPlugin.Current;

                    if (host == null ||
                        !host.Enabled)
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "move",
                            "managed_region_commands_disabled");
                        return;
                    }

                    if (!host.TryMoveRegion(
                            regionId,
                            gridX,
                            gridY,
                            out string error,
                            out int oldGridX,
                            out int oldGridY))
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            "move",
                            error);
                        return;
                    }

                    PublishOperationState(
                        "region.control.operation.completed",
                        nexEvent,
                        operationId,
                        "move",
                        "region_moved",
                        new Dictionary<string, string>
                        {
                            ["region_id"] =
                                regionId.ToString(),
                            ["old_grid_x"] =
                                oldGridX.ToString(),
                            ["old_grid_y"] =
                                oldGridY.ToString(),
                            ["grid_x"] =
                                gridX.ToString(),
                            ["grid_y"] =
                                gridY.ToString()
                        });
                }
                catch (Exception e)
                {
                    m_Log.Error(
                        "[NEX-NODE]: Managed move-region command failed.",
                        e);

                    PublishOperationState(
                        "region.control.operation.failed",
                        nexEvent,
                        operationId,
                        "move",
                        "region_move_unhandled_failure");
                }
            });
        }

        private void HandleRegionLifecycleCommand(
            NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(
                    nexEvent) ||
                !TryCommandData(
                    nexEvent,
                    "operation_id",
                    out string operationId) ||
                !TryCommandData(
                    nexEvent,
                    "action",
                    out string action))
            {
                return;
            }

            action =
                action.Trim()
                    .ToLowerInvariant();

            if (action != "start" &&
                action != "stop" &&
                action != "restart" &&
                action != "retire")
            {
                PublishOperationState(
                    "region.control.operation.failed",
                    nexEvent,
                    operationId,
                    "lifecycle",
                    "invalid_lifecycle_action");
                return;
            }

            PublishOperationState(
                "region.control.operation.accepted",
                nexEvent,
                operationId,
                action,
                "accepted");

            _ = Task.Run(() =>
            {
                try
                {
                    if (!TryCommandData(
                            nexEvent,
                            "region_id",
                            out string regionIdRaw) ||
                        !UUID.TryParse(
                            regionIdRaw,
                            out UUID regionId) ||
                        regionId.IsZero())
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            action,
                            "invalid_command_payload");
                        return;
                    }

                    NexVerseManagedRegionHostPlugin host =
                        NexVerseManagedRegionHostPlugin.Current;

                    if (host == null ||
                        !host.Enabled)
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            action,
                            "managed_region_commands_disabled");
                        return;
                    }

                    bool success;
                    string error;
                    string regionName;

                    switch (action)
                    {
                        case "start":
                            success =
                                host.TryStartRegion(
                                    regionId,
                                    out error,
                                    out regionName);
                            break;

                        case "stop":
                            success =
                                host.TryStopRegion(
                                    regionId,
                                    out error,
                                    out regionName);
                            break;

                        case "restart":
                            success =
                                host.TryRestartRegion(
                                    regionId,
                                    out error,
                                    out regionName);
                            break;

                        default:
                            success =
                                host.TryRetireRegion(
                                    regionId,
                                    out error,
                                    out regionName);
                            break;
                    }

                    if (!success)
                    {
                        PublishOperationState(
                            "region.control.operation.failed",
                            nexEvent,
                            operationId,
                            action,
                            error);
                        return;
                    }

                    string completedMessage =
                        action switch
                        {
                            "start" => "region_started",
                            "stop" => "region_stopped",
                            "restart" => "region_restarted",
                            _ => "region_retired"
                        };

                    PublishOperationState(
                        "region.control.operation.completed",
                        nexEvent,
                        operationId,
                        action,
                        completedMessage,
                        new Dictionary<string, string>
                        {
                            ["region_id"] =
                                regionId.ToString(),
                            ["region_name"] =
                                regionName ?? string.Empty,
                            ["action"] =
                                action
                        });
                }
                catch (Exception e)
                {
                    m_Log.Error(
                        "[NEX-NODE]: Managed region lifecycle command failed.",
                        e);

                    PublishOperationState(
                        "region.control.operation.failed",
                        nexEvent,
                        operationId,
                        action,
                        "region_lifecycle_unhandled_failure");
                }
            });
        }


        private void HandleIarCommand(NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(nexEvent)
                || !TryCommandData(nexEvent, "operation_id", out string operationRaw)
                || !UUID.TryParse(operationRaw, out UUID operationId)
                || !TryCommandData(nexEvent, "region_id", out string regionRaw)
                || !UUID.TryParse(regionRaw, out UUID regionId)
                || !TryCommandData(nexEvent, "user_id", out string userRaw)
                || !UUID.TryParse(userRaw, out UUID userId)
                || !TryCommandData(nexEvent, "action", out string action)
                || !TryCommandData(nexEvent, "file_name", out string fileName)
                || !TryCommandData(nexEvent, "inventory_path", out string inventoryPath))
                return;

            if (!m_Scenes.TryGetValue(regionId, out Scene scene))
            {
                PublishIarState(nexEvent, operationId, regionId, action, "failed", "region_not_loaded", 0, 0);
                return;
            }

            IOglIarOperations operations = scene.RequestModuleInterface<IOglIarOperations>();
            UserAccount user = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, userId);
            if (operations == null || user == null)
            {
                PublishIarState(nexEvent, operationId, regionId, action, "failed",
                    operations == null ? "iar_management_unavailable" : "user_not_found", 0, 0);
                return;
            }

            void Changed(OglIarOperation operation)
            {
                if (operation.RequestId != operationId) return;
                string state = operation.State.ToString().ToLowerInvariant();
                PublishIarState(nexEvent, operationId, regionId, action, state, operation.Error, operation.ItemCount, operation.FilteredCount, operation.ProgressPercent, operation.ProgressPhase);
                if (operation.State == OglIarOperationState.Completed || operation.State == OglIarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }

            operations.OperationChanged += Changed;
            try
            {
                bool dryRun = nexEvent.Data.TryGetValue("dry_run", out string dryRaw) && bool.TryParse(dryRaw, out bool dry) && dry;
                bool merge = nexEvent.Data.TryGetValue("merge", out string mergeRaw) && bool.TryParse(mergeRaw, out bool mergeParsed) && mergeParsed;
                OglIarOperation operation = string.Equals(action, "export", StringComparison.OrdinalIgnoreCase)
                    ? operations.StartExport(operationId, user, inventoryPath, fileName)
                    : string.Equals(action, "import", StringComparison.OrdinalIgnoreCase)
                        ? operations.StartImport(operationId, user, inventoryPath, fileName, merge, dryRun)
                        : throw new InvalidOperationException("unsupported_iar_action");

                PublishIarState(nexEvent, operationId, regionId, action, operation.State.ToString().ToLowerInvariant(), operation.Error, operation.ItemCount, operation.FilteredCount, operation.ProgressPercent, operation.ProgressPhase);
                if (operation.State == OglIarOperationState.Completed || operation.State == OglIarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }
            catch (Exception e)
            {
                operations.OperationChanged -= Changed;
                PublishIarState(nexEvent, operationId, regionId, action, "failed", e.Message, 0, 0);
            }
        }

        private void PublishIarState(NexEvent source, UUID operationId, UUID regionId, string action, string state, string message, int itemCount, int filteredCount, int progress = 0, string phase = "")
        {
            Publish(new NexEvent(
                "archive.iar.operation." + state,
                "opengenesislink.simulator",
                new Dictionary<string, string>
                {
                    ["operation_id"] = operationId.ToString(),
                    ["node_id"] = m_NodeId,
                    ["region_id"] = regionId.ToString(),
                    ["action"] = action ?? string.Empty,
                    ["state"] = state ?? "unknown",
                    ["message"] = message ?? string.Empty,
                    ["item_count"] = itemCount.ToString(),
                    ["filtered_count"] = filteredCount.ToString(),
                    ["progress"] = progress.ToString(),
                    ["phase"] = phase ?? string.Empty
                },
                source.CorrelationId));
        }

        private void HandleOarCommand(NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(nexEvent)
                || !TryCommandData(nexEvent, "operation_id", out string operationRaw)
                || !Guid.TryParse(operationRaw, out Guid operationId)
                || !TryCommandData(nexEvent, "region_id", out string regionRaw)
                || !UUID.TryParse(regionRaw, out UUID regionId)
                || !TryCommandData(nexEvent, "action", out string action)
                || !TryCommandData(nexEvent, "file_name", out string fileName))
                return;

            if (!m_Scenes.TryGetValue(regionId, out Scene scene))
            {
                PublishOarState(nexEvent, operationId, regionId, action, "failed", "region_not_loaded");
                return;
            }

            IOglOarOperations operations = scene.RequestModuleInterface<IOglOarOperations>();
            if (operations == null)
            {
                PublishOarState(nexEvent, operationId, regionId, action, "failed", "oar_management_unavailable");
                return;
            }

            void Changed(OglOarOperation operation)
            {
                if (operation.RequestId != operationId)
                    return;
                string state = operation.State.ToString().ToLowerInvariant();
                PublishOarState(nexEvent, operationId, regionId, action, state, operation.Error, operation.ProgressPercent, operation.ProgressPhase, operation.Inspection);
                if (operation.State == OglOarOperationState.Completed || operation.State == OglOarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }

            operations.OperationChanged += Changed;
            try
            {
                bool dryRun = nexEvent.Data.TryGetValue("dry_run", out string dryRaw)
                    && bool.TryParse(dryRaw, out bool parsedDry)
                    && parsedDry;

                OglOarOperation operation = string.Equals(action, "export", StringComparison.OrdinalIgnoreCase)
                    ? operations.StartExport(operationId, fileName)
                    : string.Equals(action, "import", StringComparison.OrdinalIgnoreCase)
                        ? operations.StartImport(operationId, fileName, dryRun)
                        : throw new InvalidOperationException("unsupported_oar_action");

                PublishOarState(
                    nexEvent,
                    operationId,
                    regionId,
                    action,
                    operation.State.ToString().ToLowerInvariant(),
                    operation.Error, operation.ProgressPercent, operation.ProgressPhase, operation.Inspection);

                if (operation.State == OglOarOperationState.Completed || operation.State == OglOarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }
            catch (Exception e)
            {
                operations.OperationChanged -= Changed;
                PublishOarState(nexEvent, operationId, regionId, action, "failed", e.Message);
            }
        }

        private void PublishOarState(
            NexEvent source,
            Guid operationId,
            UUID regionId,
            string action,
            string state,
            string message,
            int progress = 0,
            string phase = "",
            OglOarInspection inspection = null)
        {
            Dictionary<string, string> data =
                new Dictionary<string, string>
                {
                    ["operation_id"] = operationId.ToString(),
                    ["node_id"] = m_NodeId,
                    ["region_id"] = regionId.ToString(),
                    ["action"] = action ?? string.Empty,
                    ["state"] = state ?? "unknown",
                    ["message"] = message ?? string.Empty,
                    ["progress"] = progress.ToString(),
                    ["phase"] = phase ?? string.Empty
                };

            if (inspection != null)
            {
                data["archive_sha256"] = inspection.Sha256 ?? string.Empty;
                data["archive_bytes"] = inspection.SizeBytes.ToString();
                data["archive_valid"] = inspection.Valid.ToString();
            }

            Publish(new NexEvent(
                "archive.oar.operation." + state,
                "nexverse.simulator",
                data,
                source.CorrelationId));
        }

        private void HandleNodeControlCommand(NexEvent nexEvent)
        {
            if (!IsCommandForThisNode(nexEvent) ||
                !TryCommandData(nexEvent, "action", out string action))
                return;

            action = action.Trim().ToLowerInvariant();
            bool accepted = true;
            switch (action)
            {
                case "maintenance_on":
                    m_MaintenanceMode = true;
                    break;
                case "maintenance_off":
                    m_MaintenanceMode = false;
                    break;
                case "drain":
                    m_Draining = true;
                    m_MaintenanceMode = true;
                    break;
                case "resume":
                    m_Draining = false;
                    m_MaintenanceMode = false;
                    break;
                default:
                    accepted = false;
                    break;
            }

            Publish(new NexEvent(
                accepted ? "node.control.completed" : "node.control.failed",
                "nexverse.simulator",
                new Dictionary<string, string>
                {
                    ["node_id"] = m_NodeId,
                    ["action"] = action,
                    ["maintenance_mode"] = m_MaintenanceMode.ToString(),
                    ["draining"] = m_Draining.ToString(),
                    ["message"] = accepted ? "node_control_applied" : "invalid_node_control_action"
                }));
            PublishHeartbeatSafe();
        }

        private bool IsCommandForThisNode(
            NexEvent nexEvent)
        {
            return
                TryCommandData(
                    nexEvent,
                    "target_node_id",
                    out string targetNodeId) &&
                string.Equals(
                    targetNodeId,
                    m_NodeId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCommandData(
            NexEvent nexEvent,
            string key,
            out string value)
        {
            value = string.Empty;

            if (nexEvent?.Data == null ||
                !nexEvent.Data.TryGetValue(
                    key,
                    out string raw) ||
                string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            value = raw.Trim();
            return true;
        }

        private static bool TryCommandInt(
            NexEvent nexEvent,
            string key,
            out int value)
        {
            value = 0;

            return
                TryCommandData(
                    nexEvent,
                    key,
                    out string raw) &&
                int.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
        }

        private void PublishOperationState(
            string eventName,
            NexEvent request,
            string operationId,
            string operation,
            string message,
            IReadOnlyDictionary<string, string> additional = null)
        {
            Dictionary<string, string> data =
                new Dictionary<string, string>
                {
                    ["operation_id"] =
                        operationId ?? string.Empty,
                    ["operation"] =
                        operation ?? string.Empty,
                    ["node_id"] =
                        m_NodeId,
                    ["message"] =
                        message ?? string.Empty
                };

            if (request?.Data != null &&
                request.Data.TryGetValue(
                    "region_id",
                    out string requestedRegionId) &&
                requestedRegionId != null)
            {
                data["region_id"] =
                    requestedRegionId;
            }

            if (additional != null)
            {
                foreach (KeyValuePair<string, string> item in additional)
                    data[item.Key] = item.Value ?? string.Empty;
            }

            Publish(new NexEvent(
                eventName,
                "nexverse.simulator",
                data,
                request?.CorrelationId));
        }

        private void HandleInbound(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/json; charset=utf-8";

            if (request == null ||
                !string.Equals(
                    request.HttpMethod,
                    "POST",
                    StringComparison.OrdinalIgnoreCase))
            {
                WriteInbound(
                    response,
                    HttpStatusCode.MethodNotAllowed,
                    new { error = "method_not_allowed" });
                return;
            }

            byte[] payload;
            using (MemoryStream buffer = new MemoryStream())
            {
                request.InputStream.CopyTo(buffer);
                payload = buffer.ToArray();
            }

            if (payload.Length == 0 || payload.Length > 1024 * 1024)
            {
                WriteInbound(
                    response,
                    HttpStatusCode.BadRequest,
                    new { error = "invalid_event_payload" });
                return;
            }

            if (!NexBusProtocol.Verify(
                payload,
                m_SharedKey,
                request.Headers?["X-NexBus-Signature"]))
            {
                WriteInbound(
                    response,
                    HttpStatusCode.Unauthorized,
                    new { error = "invalid_signature" });
                return;
            }

            try
            {
                NexEvent nexEvent = NexBusProtocol.Deserialize(payload);
                bool accepted = m_Bus.Receive(nexEvent);
                WriteInbound(
                    response,
                    HttpStatusCode.Accepted,
                    new
                    {
                        accepted,
                        event_id = nexEvent.EventId
                    });
            }
            catch
            {
                WriteInbound(
                    response,
                    HttpStatusCode.BadRequest,
                    new { error = "invalid_event" });
            }
        }

        private static void WriteInbound(
            IOSHttpResponse response,
            HttpStatusCode status,
            object payload)
        {
            response.StatusCode = (int)status;
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload);
        }

        private sealed class ObjectMessageRateState
        {
            public object SyncRoot { get; } =
                new object();

            public long WindowSecond;
            public long LastSeenSecond;
            public int Count;
        }

        private sealed class SimulatorNexEventTransport : INexEventTransport
        {
            private readonly string m_NodeId;
            private readonly Uri m_Peer;
            private readonly string m_SharedKey;
            private readonly BlockingCollection<NexEvent> m_Queue;
            private readonly CancellationTokenSource m_Cancellation =
                new CancellationTokenSource();
            private readonly HttpClient m_Client;
            private readonly Task m_Worker;
            private int m_Disposed;

            public SimulatorNexEventTransport(
                string nodeId,
                Uri peer,
                string sharedKey,
                int queueCapacity,
                int requestTimeoutMilliseconds)
            {
                m_NodeId = nodeId;
                m_Peer = peer;
                m_SharedKey = sharedKey;
                m_Queue = new BlockingCollection<NexEvent>(
                    new ConcurrentQueue<NexEvent>(),
                    queueCapacity);
                m_Client = new HttpClient
                {
                    Timeout = TimeSpan.FromMilliseconds(requestTimeoutMilliseconds)
                };
                m_Worker = Task.Run(WorkerLoop);
            }

            public void Send(NexEvent nexEvent)
            {
                if (nexEvent == null ||
                    Volatile.Read(ref m_Disposed) != 0)
                    return;

                if (!m_Queue.TryAdd(nexEvent))
                {
                    m_Log.WarnFormat(
                        "[NEX-NODE]: Outbound NexBus queue full; dropping {0} {1}",
                        nexEvent.EventId,
                        nexEvent.Name);
                }
            }

            private void WorkerLoop()
            {
                try
                {
                    foreach (NexEvent nexEvent in
                             m_Queue.GetConsumingEnumerable(m_Cancellation.Token))
                    {
                        SendOne(nexEvent);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }

            private void SendOne(NexEvent nexEvent)
            {
                byte[] payload = NexBusProtocol.Serialize(nexEvent);
                string signature = NexBusProtocol.Sign(payload, m_SharedKey);

                try
                {
                    using HttpRequestMessage request =
                        new HttpRequestMessage(HttpMethod.Post, m_Peer);
                    request.Headers.TryAddWithoutValidation(
                        "X-NexBus-Node",
                        m_NodeId);
                    request.Headers.TryAddWithoutValidation(
                        "X-NexBus-Schema",
                        NexVerse.Core.NexVersePlatform.NexBusSchemaVersion);
                    request.Headers.TryAddWithoutValidation(
                        "X-NexBus-Signature",
                        "sha256=" + signature);
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/json");

                    using HttpResponseMessage response = m_Client.Send(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        m_Cancellation.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        m_Log.WarnFormat(
                            "[NEX-NODE]: NexBus peer rejected {0} with HTTP {1}",
                            nexEvent.EventId,
                            (int)response.StatusCode);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    m_Log.WarnFormat(
                        "[NEX-NODE]: NexBus delivery failed for {0}: {1}",
                        nexEvent.EventId,
                        e.Message);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref m_Disposed, 1) != 0)
                    return;

                m_Queue.CompleteAdding();
                m_Cancellation.Cancel();

                try
                {
                    m_Worker.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }

                m_Client.Dispose();
                m_Cancellation.Dispose();
                m_Queue.Dispose();
            }
        }
    }
}
