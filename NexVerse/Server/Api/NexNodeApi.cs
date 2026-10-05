// SPDX-License-Identifier: MPL-2.0

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Security;
using NexVerse.Core.Messaging;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexNodeApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly NexNodeRegistry m_Registry;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly bool m_DistributedTransportEnabled;
        private readonly INexEventBus m_EventBus;

        public NexNodeApi(
            NexNodeRegistry registry,
            NexApiAuthenticator authenticator,
            bool distributedTransportEnabled,
            INexEventBus eventBus)
        {
            m_Registry =
                registry ??
                throw new ArgumentNullException(nameof(registry));
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_DistributedTransportEnabled =
                distributedTransportEnabled;
            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            bool control = request != null && string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && (request.UriPath ?? string.Empty).EndsWith("/control", StringComparison.OrdinalIgnoreCase);
            if (!control && !RequireGet(request, response)) return;
            if (!Authenticate(request, response, control ? NexScopes.SimulatorsManage : NexScopes.SimulatorsRead)) return;

            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');

            if (string.Equals(
                    path,
                    "/api/v1/nodes",
                    StringComparison.OrdinalIgnoreCase))
            {
                HandleList(response);
                return;
            }

            const string prefix =
                "/api/v1/nodes/";

            if (control && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.EndsWith("/control", StringComparison.OrdinalIgnoreCase))
            {
                string nodeId = Uri.UnescapeDataString(path.Substring(prefix.Length, path.Length - prefix.Length - "/control".Length)).Trim('/');
                HandleControl(request, response, nodeId);
                return;
            }

            if (path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string nodeId =
                    Uri.UnescapeDataString(
                        path.Substring(prefix.Length));

                if (string.IsNullOrWhiteSpace(nodeId) ||
                    nodeId.Contains('/'))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_node_id",
                        "A single simulator node ID is required.");
                    return;
                }

                HandleGet(
                    response,
                    nodeId);
                return;
            }

            WriteError(
                response,
                HttpStatusCode.NotFound,
                "not_found",
                "Unknown NexVerse simulator-node endpoint.");
        }

        private void HandleList(
            IOSHttpResponse response)
        {
            NexNodeSnapshot[] nodes =
                m_Registry.List().ToArray();

            string correlationId =
                NexApiRequestContext.Ensure(response);

            WriteJson(response, new
            {
                generated_at =
                    DateTimeOffset.UtcNow,
                transport_enabled =
                    m_DistributedTransportEnabled,
                stale_after_seconds =
                    m_Registry.StaleAfterSeconds,
                count =
                    nodes.Length,
                luna_texture =
                    LunaTextureAggregatePayload(
                        nodes.Where(node =>
                            node.State == "online")),
                nodes =
                    nodes.Select(NodePayload).ToArray(),
                correlation_id =
                    correlationId
            });
        }

        private void HandleGet(
            IOSHttpResponse response,
            string nodeId)
        {
            NexNodeSnapshot node =
                m_Registry.Get(nodeId);

            if (node == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "node_not_found",
                    "Simulator node was not observed by the NexVerse NodeAgent registry.");
                return;
            }

            string correlationId =
                NexApiRequestContext.Ensure(response);

            WriteJson(response, new
            {
                generated_at =
                    DateTimeOffset.UtcNow,
                transport_enabled =
                    m_DistributedTransportEnabled,
                stale_after_seconds =
                    m_Registry.StaleAfterSeconds,
                node =
                    NodePayload(node),
                correlation_id =
                    correlationId
            });
        }


        private void HandleControl(IOSHttpRequest request, IOSHttpResponse response, string nodeId)
        {
            NexNodeSnapshot node = m_Registry.Get(nodeId);
            if (node == null) { WriteError(response, HttpStatusCode.NotFound, "node_not_found", "Simulator node was not observed."); return; }
            if (node.State != "online") { WriteError(response, HttpStatusCode.Conflict, "node_not_online", "Node control requires an online NodeAgent."); return; }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(request.InputStream);
                string action = doc.RootElement.TryGetProperty("action", out JsonElement a) ? (a.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (action != "maintenance_on" && action != "maintenance_off" && action != "drain" && action != "resume")
                { WriteError(response, HttpStatusCode.BadRequest, "invalid_action", "Supported actions: maintenance_on, maintenance_off, drain, resume."); return; }
                m_EventBus.Publish(new NexEvent("node.control.requested", "nexverse.robust", new System.Collections.Generic.Dictionary<string,string>
                {
                    ["target_node_id"] = nodeId,
                    ["action"] = action
                }));
                WriteJson(response, new { status = "accepted", node_id = nodeId, action }, HttpStatusCode.Accepted);
            }
            catch (JsonException)
            { WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "A JSON object with action is required."); }
        }

        private static object NodePayload(
            NexNodeSnapshot node)
        {
            return new
            {
                node_id =
                    node.NodeId,
                hostname =
                    node.Hostname,
                server_version =
                    node.ServerVersion,
                state =
                    node.State,
                uptime_seconds =
                    node.UptimeSeconds,
                process_id =
                    node.ProcessId,
                working_set_bytes =
                    node.WorkingSetBytes,
                cpu_seconds =
                    node.CpuSeconds,
                disk_free_bytes =
                    node.DiskFreeBytes,
                disk_total_bytes =
                    node.DiskTotalBytes,
                health = new
                {
                    heartbeat = node.State,
                    disk_free_percent = node.DiskTotalBytes > 0 ? Math.Round(100.0 * node.DiskFreeBytes / node.DiskTotalBytes, 2) : (double?)null,
                    accepting_managed_commands = node.State == "online" && node.ManagedRegionCommands
                },
                region_count =
                    node.RegionCount,
                agent_count =
                    node.AgentCount,
                managed_region_commands =
                    node.ManagedRegionCommands,
                maintenance_mode = node.MaintenanceMode,
                draining = node.Draining,
                luna_texture =
                    LunaTextureNodePayload(node),
                last_seen =
                    node.LastSeen,
                last_event_at =
                    node.LastEventAt,
                regions =
                    node.Regions.Select(region => new
                    {
                        region_id =
                            region.RegionId,
                        name =
                            region.Name,
                        server_uri =
                            region.ServerUri,
                        size_x =
                            region.SizeX,
                        size_y =
                            region.SizeY,
                        agent_count =
                            region.AgentCount,
                        last_seen =
                            region.LastSeen
                    }).ToArray()
            };
        }

        private static object LunaTextureNodePayload(
            NexNodeSnapshot node)
        {
            return new
            {
                diagnostic_count =
                    node.LunaTextureDiagnosticCount,
                occurrence_count =
                    node.LunaTextureOccurrenceCount,
                regions_affected =
                    node.LunaTextureRegionsAffected,
                last_seen =
                    node.LunaTextureLastSeen,
                classifications =
                    node.LunaTextureClassifications
            };
        }

        private static object LunaTextureAggregatePayload(
            System.Collections.Generic.IEnumerable<NexNodeSnapshot> source)
        {
            NexNodeSnapshot[] nodes =
                (source ?? Array.Empty<NexNodeSnapshot>())
                    .ToArray();

            System.Collections.Generic.Dictionary<string, long> classifications =
                new System.Collections.Generic.Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (NexNodeSnapshot node in nodes)
            {
                foreach (System.Collections.Generic.KeyValuePair<string, long> item
                         in node.LunaTextureClassifications)
                {
                    classifications.TryGetValue(
                        item.Key,
                        out long current);
                    classifications[item.Key] =
                        current + Math.Max(0, item.Value);
                }
            }

            DateTimeOffset? lastSeen =
                nodes
                    .Where(node =>
                        node.LunaTextureLastSeen.HasValue)
                    .Select(node =>
                        node.LunaTextureLastSeen)
                    .OrderByDescending(value =>
                        value)
                    .FirstOrDefault();

            return new
            {
                source = "online_nodes",
                node_count =
                    nodes.Length,
                diagnostic_count =
                    nodes.Sum(node =>
                        Math.Max(
                            0,
                            node.LunaTextureDiagnosticCount)),
                occurrence_count =
                    nodes.Sum(node =>
                        Math.Max(
                            0L,
                            node.LunaTextureOccurrenceCount)),
                regions_affected =
                    nodes.Sum(node =>
                        Math.Max(
                            0,
                            node.LunaTextureRegionsAffected)),
                last_seen =
                    lastSeen,
                classifications
            };
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    scope,
                    out NexPrincipal _,
                    out UserAccount _,
                    out int statusCode,
                    out string error))
            {
                return true;
            }

            response.AddHeader(
                "WWW-Authenticate",
                "Bearer");

            WriteError(
                response,
                (HttpStatusCode)statusCode,
                error,
                "Authentication or simulators:read authorization is required.");

            return false;
        }

        private static bool RequireGet(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (request != null &&
                string.Equals(
                    request.HttpMethod,
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                "GET is required.");

            return false;
        }

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string message)
        {
            string correlationId =
                NexApiRequestContext.Ensure(response);

            WriteJson(
                response,
                new
                {
                    error,
                    message,
                    correlation_id =
                        correlationId
                },
                status);
        }

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status =
                HttpStatusCode.OK)
        {
            response.KeepAlive = false;
            response.StatusCode =
                (int)status;
            response.ContentType =
                "application/json; charset=utf-8";
            response.AddHeader(
                "Cache-Control",
                "no-store");
            response.AddHeader(
                "X-Content-Type-Options",
                "nosniff");
            response.RawBuffer =
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        payload,
                        s_Json));
        }
    }
}
