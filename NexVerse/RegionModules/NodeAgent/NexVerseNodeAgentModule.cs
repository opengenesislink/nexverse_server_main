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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

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
                ["region_count"] = scenes.Length.ToString(),
                ["agent_count"] = agents.ToString(),
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
