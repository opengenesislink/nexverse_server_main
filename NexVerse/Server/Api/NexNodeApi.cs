// SPDX-License-Identifier: MPL-2.0

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Security;
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

        public NexNodeApi(
            NexNodeRegistry registry,
            NexApiAuthenticator authenticator,
            bool distributedTransportEnabled)
        {
            m_Registry =
                registry ??
                throw new ArgumentNullException(nameof(registry));
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_DistributedTransportEnabled =
                distributedTransportEnabled;
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            if (!Authenticate(request, response))
                return;

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
                region_count =
                    node.RegionCount,
                agent_count =
                    node.AgentCount,
                managed_region_commands =
                    node.ManagedRegionCommands,
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

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    NexScopes.SimulatorsRead,
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
