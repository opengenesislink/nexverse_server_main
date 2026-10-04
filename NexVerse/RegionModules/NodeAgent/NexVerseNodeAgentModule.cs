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
        INexVerseEventBusModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexVerseNodeAgentModule));

        private readonly ConcurrentDictionary<UUID, Scene> m_Scenes =
            new ConcurrentDictionary<UUID, Scene>();

        private bool m_Enabled;
        private string m_NodeId = string.Empty;
        private string m_SharedKey = string.Empty;
        private string m_InboundPath = "/internal/nexbus/v1/events";
        private int m_HeartbeatSeconds = 30;
        private SimulatorNexEventTransport m_Transport;
        private DistributedNexEventBus m_Bus;
        private Timer m_HeartbeatTimer;
        private IDisposable m_CreateRegionSubscription;
        private IDisposable m_MoveRegionSubscription;
        private IDisposable m_RegionLifecycleSubscription;
        private IDisposable m_NodeControlSubscription;
        private IDisposable m_OarControlSubscription;
        private IDisposable m_IarControlSubscription;
        private volatile bool m_MaintenanceMode;
        private volatile bool m_Draining;
        private DateTimeOffset m_StartedAt;

        public string NodeId => m_NodeId;
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
                ["regions"] = string.Join(
                    ";",
                    scenes
                        .OrderBy(x => x.RegionInfo.RegionName, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.RegionInfo.RegionID + "|" + x.RegionInfo.RegionName))
            };
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
                action != "restart")
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

                        default:
                            success =
                                host.TryRestartRegion(
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
                            _ => "region_restarted"
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
                PublishIarState(nexEvent, operationId, regionId, action, state, operation.Error, operation.ItemCount, operation.FilteredCount);
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

                PublishIarState(nexEvent, operationId, regionId, action, operation.State.ToString().ToLowerInvariant(), operation.Error, operation.ItemCount, operation.FilteredCount);
                if (operation.State == OglIarOperationState.Completed || operation.State == OglIarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }
            catch (Exception e)
            {
                operations.OperationChanged -= Changed;
                PublishIarState(nexEvent, operationId, regionId, action, "failed", e.Message, 0, 0);
            }
        }

        private void PublishIarState(NexEvent source, UUID operationId, UUID regionId, string action, string state, string message, int itemCount, int filteredCount)
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
                    ["filtered_count"] = filteredCount.ToString()
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
                PublishOarState(nexEvent, operationId, regionId, action, state, operation.Error);
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
                    operation.Error);

                if (operation.State == OglOarOperationState.Completed || operation.State == OglOarOperationState.Failed)
                    operations.OperationChanged -= Changed;
            }
            catch (Exception e)
            {
                operations.OperationChanged -= Changed;
                PublishOarState(nexEvent, operationId, regionId, action, "failed", e.Message);
            }
        }

        private void PublishOarState(NexEvent source, Guid operationId, UUID regionId, string action, string state, string message)
        {
            Publish(new NexEvent(
                "archive.oar.operation." + state,
                "nexverse.simulator",
                new Dictionary<string, string>
                {
                    ["operation_id"] = operationId.ToString(),
                    ["node_id"] = m_NodeId,
                    ["region_id"] = regionId.ToString(),
                    ["action"] = action ?? string.Empty,
                    ["state"] = state ?? "unknown",
                    ["message"] = message ?? string.Empty
                },
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
