// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using NexVerse.Core.Messaging;
using NexVerse.Core.Observability;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal sealed class NexBusWireEvent
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; }

        [JsonPropertyName("event_id")]
        public Guid EventId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; }

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, string> Data { get; set; }

        public static NexBusWireEvent FromEvent(NexEvent nexEvent)
        {
            return new NexBusWireEvent
            {
                Schema = Core.NexVersePlatform.NexBusSchemaVersion,
                EventId = nexEvent.EventId,
                Timestamp = nexEvent.Timestamp,
                Name = nexEvent.Name,
                Source = nexEvent.Source,
                CorrelationId = nexEvent.CorrelationId,
                Data = new Dictionary<string, string>(nexEvent.Data)
            };
        }

        public NexEvent ToEvent()
        {
            if (!string.Equals(Schema, Core.NexVersePlatform.NexBusSchemaVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported NexBus schema version.");

            return new NexEvent(
                EventId,
                Timestamp,
                Name,
                Source,
                Data ?? new Dictionary<string, string>(),
                CorrelationId);
        }
    }

    internal static class NexBusHmac
    {
        public static string Sign(byte[] payload, string sharedKey)
        {
            using HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedKey));
            return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
        }

        public static bool Verify(byte[] payload, string sharedKey, string supplied)
        {
            if (string.IsNullOrWhiteSpace(supplied))
                return false;

            string value = supplied.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
                ? supplied.Substring("sha256=".Length)
                : supplied;

            byte[] expected;
            byte[] actual;

            try
            {
                expected = Convert.FromHexString(Sign(payload, sharedKey));
                actual = Convert.FromHexString(value);
            }
            catch
            {
                return false;
            }

            return expected.Length == actual.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

    internal sealed class HttpNexEventTransport : INexEventTransport
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(HttpNexEventTransport));
        private static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions();

        private readonly string m_NodeId;
        private readonly string m_SharedKey;
        private readonly IReadOnlyList<Uri> m_Peers;
        private readonly BlockingCollection<NexEvent> m_Queue;
        private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
        private readonly HttpClient m_Client;
        private readonly Task m_Worker;
        private int m_Disposed;

        public HttpNexEventTransport(
            string nodeId,
            IEnumerable<string> peerUrls,
            string sharedKey,
            int queueCapacity,
            int requestTimeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("NexBus node ID is required.", nameof(nodeId));
            if (string.IsNullOrEmpty(sharedKey) || Encoding.UTF8.GetByteCount(sharedKey) < 32)
                throw new ArgumentException("NexBus shared key must contain at least 32 UTF-8 bytes.", nameof(sharedKey));

            m_NodeId = nodeId.Trim();
            m_SharedKey = sharedKey;
            m_Peers = (peerUrls ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new Uri(x.Trim(), UriKind.Absolute))
                .Distinct()
                .ToArray();

            m_Queue = new BlockingCollection<NexEvent>(
                new ConcurrentQueue<NexEvent>(),
                Math.Max(128, queueCapacity));

            m_Client = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(250, requestTimeoutMilliseconds))
            };

            m_Worker = Task.Run(WorkerLoop);
        }

        public void Send(NexEvent nexEvent)
        {
            if (nexEvent == null || Volatile.Read(ref m_Disposed) != 0 || m_Peers.Count == 0)
                return;

            if (!m_Queue.TryAdd(nexEvent))
            {
                NexMetricsRegistry.Default.IncrementCounter(
                    "nexverse_nexbus_events_dropped_total",
                    "NexBus events dropped before outbound delivery.");
                m_Log.WarnFormat("[NEXBUS]: Outbound queue is full; dropping event {0} ({1}).", nexEvent.EventId, nexEvent.Name);
                return;
            }

            NexMetricsRegistry.Default.IncrementCounter(
                "nexverse_nexbus_events_enqueued_total",
                "NexBus events accepted by the outbound queue.");
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (NexEvent nexEvent in m_Queue.GetConsumingEnumerable(m_Cancellation.Token))
                    SendToPeers(nexEvent);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                m_Log.Error("[NEXBUS]: Outbound transport worker stopped unexpectedly.", e);
            }
        }

        private void SendToPeers(NexEvent nexEvent)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                NexBusWireEvent.FromEvent(nexEvent),
                s_Json);
            string signature = NexBusHmac.Sign(payload, m_SharedKey);

            foreach (Uri peer in m_Peers)
            {
                try
                {
                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, peer);
                    request.Headers.TryAddWithoutValidation("X-NexBus-Node", m_NodeId);
                    request.Headers.TryAddWithoutValidation("X-NexBus-Schema", Core.NexVersePlatform.NexBusSchemaVersion);
                    request.Headers.TryAddWithoutValidation("X-NexBus-Signature", "sha256=" + signature);
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    using HttpResponseMessage response = m_Client.Send(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        m_Cancellation.Token);

                    NexMetricsRegistry.Default.IncrementCounter(
                        "nexverse_nexbus_peer_delivery_total",
                        "NexBus peer delivery attempts by result.",
                        1,
                        new Dictionary<string, string>
                        {
                            ["result"] = response.IsSuccessStatusCode ? "success" : "rejected"
                        });

                    if (!response.IsSuccessStatusCode)
                    {
                        m_Log.WarnFormat(
                            "[NEXBUS]: Peer {0} rejected event {1} with HTTP {2}.",
                            peer,
                            nexEvent.EventId,
                            (int)response.StatusCode);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (m_Cancellation.IsCancellationRequested)
                        return;

                    m_Log.WarnFormat("[NEXBUS]: Timed out delivering event {0} to {1}.", nexEvent.EventId, peer);
                }
                catch (Exception e)
                {
                    NexMetricsRegistry.Default.IncrementCounter(
                        "nexverse_nexbus_peer_delivery_total",
                        "NexBus peer delivery attempts by result.",
                        1,
                        new Dictionary<string, string>
                        {
                            ["result"] = "error"
                        });

                    m_Log.WarnFormat(
                        "[NEXBUS]: Failed delivering event {0} to {1}: {2}",
                        nexEvent.EventId,
                        peer,
                        e.Message);
                }
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

    internal sealed class NexBusHttpEndpoint
    {
        private static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions();
        private readonly DistributedNexEventBus m_Bus;
        private readonly string m_SharedKey;

        public NexBusHttpEndpoint(DistributedNexEventBus bus, string sharedKey)
        {
            m_Bus = bus ?? throw new ArgumentNullException(nameof(bus));
            m_SharedKey = sharedKey ?? throw new ArgumentNullException(nameof(sharedKey));
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            response.ContentType = "application/json; charset=utf-8";

            if (request == null || !string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                Write(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
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
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_event_payload" });
                return;
            }

            string signature = request.Headers?["X-NexBus-Signature"];
            if (!NexBusHmac.Verify(payload, m_SharedKey, signature))
            {
                Write(response, HttpStatusCode.Unauthorized, new { error = "invalid_signature" });
                return;
            }

            try
            {
                NexBusWireEvent wire = JsonSerializer.Deserialize<NexBusWireEvent>(payload, s_Json);
                NexEvent nexEvent = wire?.ToEvent();
                if (nexEvent == null)
                {
                    Write(response, HttpStatusCode.BadRequest, new { error = "invalid_event" });
                    return;
                }

                bool accepted = m_Bus.Receive(nexEvent);
                Write(response, HttpStatusCode.Accepted, new
                {
                    accepted,
                    event_id = nexEvent.EventId
                });
            }
            catch (Exception)
            {
                Write(response, HttpStatusCode.BadRequest, new { error = "invalid_event" });
            }
        }

        private static void Write(IOSHttpResponse response, HttpStatusCode status, object body)
        {
            response.StatusCode = (int)status;
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(body, s_Json);
        }
    }
}
