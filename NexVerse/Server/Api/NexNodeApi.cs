// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
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
        private readonly NexNodeCommandTracker m_Commands;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly INexAuditSink m_Audit;
        private readonly bool m_DistributedTransportEnabled;

        public NexNodeApi(
            NexNodeRegistry registry,
            NexNodeCommandTracker commands,
            NexApiAuthenticator authenticator,
            INexAuditSink audit,
            bool distributedTransportEnabled)
        {
            m_Registry =
                registry ??
                throw new ArgumentNullException(nameof(registry));
            m_Commands =
                commands ??
                throw new ArgumentNullException(nameof(commands));
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_Audit =
                audit ??
                NullNexAuditSink.Instance;
            m_DistributedTransportEnabled =
                distributedTransportEnabled;
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');

            if (string.Equals(
                    path,
                    "/api/v1/nodes",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!RequireMethod(
                        request,
                        response,
                        "GET") ||
                    !Authenticate(
                        request,
                        response,
                        NexScopes.SimulatorsRead,
                        out NexPrincipal _))
                {
                    return;
                }

                HandleList(response);
                return;
            }

            const string commandPrefix =
                "/api/v1/node-commands/";

            if (path.StartsWith(
                    commandPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!RequireMethod(
                        request,
                        response,
                        "GET") ||
                    !Authenticate(
                        request,
                        response,
                        NexScopes.SimulatorsRead,
                        out NexPrincipal _))
                {
                    return;
                }

                string commandId =
                    path.Substring(
                        commandPrefix.Length);

                if (string.IsNullOrWhiteSpace(commandId) ||
                    commandId.Contains('/'))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_command_id",
                        "A single node command UUID is required.");
                    return;
                }

                HandleCommandGet(
                    response,
                    commandId);
                return;
            }

            const string nodePrefix =
                "/api/v1/nodes/";

            if (path.StartsWith(
                    nodePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string tail =
                    path.Substring(
                        nodePrefix.Length);

                const string pingSuffix =
                    "/commands/ping";

                if (tail.EndsWith(
                        pingSuffix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireMethod(
                            request,
                            response,
                            "POST") ||
                        !Authenticate(
                            request,
                            response,
                            NexScopes.SimulatorsManage,
                            out NexPrincipal principal))
                    {
                        return;
                    }

                    string encodedNodeId =
                        tail.Substring(
                            0,
                            tail.Length -
                            pingSuffix.Length);

                    if (!TryNodeId(
                            encodedNodeId,
                            response,
                            out string nodeId))
                    {
                        return;
                    }

                    HandlePing(
                        response,
                        nodeId,
                        principal);
                    return;
                }

                if (!RequireMethod(
                        request,
                        response,
                        "GET") ||
                    !Authenticate(
                        request,
                        response,
                        NexScopes.SimulatorsRead,
                        out NexPrincipal _))
                {
                    return;
                }

                if (!TryNodeId(
                        tail,
                        response,
                        out string nodeId))
                {
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

        private void HandlePing(
            IOSHttpResponse response,
            string nodeId,
            NexPrincipal principal)
        {
            if (!m_DistributedTransportEnabled)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "node_command_transport_disabled",
                    "Distributed NexBus transport must be enabled before NodeAgent commands can be issued.");
                return;
            }

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

            if (string.Equals(
                    node.State,
                    "offline",
                    StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "node_offline",
                    "An explicitly offline simulator node cannot receive a command.");
                return;
            }

            string correlationId =
                NexApiRequestContext.Ensure(response);

            NexNodeCommandSnapshot command;

            try
            {
                command =
                    m_Commands.IssuePing(
                        node.NodeId,
                        principal?.Subject,
                        correlationId);
            }
            catch (Exception)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "node_command_publish_failed",
                    "The directed NodeAgent command could not be published.");
                return;
            }

            m_Audit.Record(
                new NexAuditEvent(
                    principal?.Subject,
                    "simulators.command.ping",
                    node.NodeId,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["command_id"] =
                            command.CommandId.ToString(),
                        ["action"] =
                            command.Action
                    }));

            WriteJson(
                response,
                new
                {
                    command =
                        CommandPayload(command),
                    status_url =
                        "/api/v1/node-commands/" +
                        command.CommandId,
                    correlation_id =
                        correlationId
                },
                HttpStatusCode.Accepted);
        }

        private void HandleCommandGet(
            IOSHttpResponse response,
            string commandId)
        {
            NexNodeCommandSnapshot command =
                m_Commands.Get(commandId);

            if (command == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "node_command_not_found",
                    "NodeAgent command was not found in the bounded command history.");
                return;
            }

            string correlationId =
                NexApiRequestContext.Ensure(response);

            WriteJson(response, new
            {
                transport_enabled =
                    m_DistributedTransportEnabled,
                command_timeout_seconds =
                    m_Commands.CommandTimeoutSeconds,
                command =
                    CommandPayload(command),
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

        private static object CommandPayload(
            NexNodeCommandSnapshot command)
        {
            return new
            {
                command_id =
                    command.CommandId,
                node_id =
                    command.NodeId,
                action =
                    command.Action,
                requested_by =
                    command.RequestedBy,
                state =
                    command.State,
                message =
                    command.Message,
                created_at =
                    command.CreatedAt,
                updated_at =
                    command.UpdatedAt,
                expires_at =
                    command.ExpiresAt,
                correlation_id =
                    command.CorrelationId
            };
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope,
            out NexPrincipal principal)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    scope,
                    out principal,
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
                "Authentication or " +
                scope +
                " authorization is required.");

            return false;
        }

        private static bool TryNodeId(
            string encodedNodeId,
            IOSHttpResponse response,
            out string nodeId)
        {
            nodeId =
                Uri.UnescapeDataString(
                    encodedNodeId ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(nodeId) ||
                nodeId.Contains('/'))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_node_id",
                    "A single simulator node ID is required.");
                return false;
            }

            return true;
        }

        private static bool RequireMethod(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string method)
        {
            if (request != null &&
                string.Equals(
                    request.HttpMethod,
                    method,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                method +
                " is required.");

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
