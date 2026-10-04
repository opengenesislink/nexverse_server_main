// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class OglOarApi : IDisposable
    {
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly NexNodeRegistry m_Nodes;
        private readonly INexEventBus m_Bus;
        private readonly IDisposable m_Subscription;
        private readonly ConcurrentDictionary<Guid, Record> m_Operations = new();

        public OglOarApi(NexApiAuthenticator authenticator, NexNodeRegistry nodes, INexEventBus bus)
        {
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            m_Bus = bus ?? throw new ArgumentNullException(nameof(bus));
            m_Subscription = m_Bus.Subscribe("*", ApplyState);
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            if (!Authenticate(request, response))
                return;

            if (string.Equals(path, "/api/v1/oar/export", StringComparison.OrdinalIgnoreCase))
            {
                Start(request, response, "export");
                return;
            }
            if (string.Equals(path, "/api/v1/oar/import", StringComparison.OrdinalIgnoreCase))
            {
                Start(request, response, "import");
                return;
            }

            const string prefix = "/api/v1/oar/operations/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(path.Substring(prefix.Length), out Guid operationId))
            {
                if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "GET ist erforderlich.");
                    return;
                }

                if (!m_Operations.TryGetValue(operationId, out Record record))
                {
                    WriteError(response, HttpStatusCode.NotFound, "operation_not_found", "OAR-Vorgang wurde nicht gefunden.");
                    return;
                }

                WriteJson(response, HttpStatusCode.OK, record.Payload());
                return;
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unbekannter OAR-Endpunkt.");
        }

        private void Start(IOSHttpRequest request, IOSHttpResponse response, string action)
        {
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "POST ist erforderlich.");
                return;
            }

            JsonDocument document;
            try
            {
                using StreamReader reader = new(request.InputStream, Encoding.UTF8, true, 1024, true);
                document = JsonDocument.Parse(reader.ReadToEnd());
            }
            catch
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "Ungueltiger JSON-Request.");
                return;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string regionId = GetString(root, "region_id");
                string fileName = GetString(root, "file_name");
                bool dryRun = root.TryGetProperty("dry_run", out JsonElement dry) && dry.ValueKind == JsonValueKind.True;

                if (string.IsNullOrWhiteSpace(regionId) || !Guid.TryParse(regionId, out _))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_region_id", "region_id muss eine gueltige UUID sein.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_file_name", "file_name ist erforderlich.");
                    return;
                }

                NexNodeSnapshot node = m_Nodes.FindNodeForRegion(regionId);
                if (node == null || !string.Equals(node.State, "online", StringComparison.OrdinalIgnoreCase))
                {
                    WriteError(response, HttpStatusCode.ServiceUnavailable, "region_node_unavailable", "Simulator der Region ist nicht erreichbar.");
                    return;
                }

                Guid operationId = Guid.NewGuid();
                Record record = new(operationId, regionId, node.NodeId, action, fileName, dryRun);
                if (!m_Operations.TryAdd(operationId, record))
                    throw new InvalidOperationException("OAR-Vorgang konnte nicht registriert werden.");

                m_Bus.Publish(new NexEvent(
                    "archive.oar.requested",
                    "opengenesislink.world-api",
                    new Dictionary<string, string>
                    {
                        ["operation_id"] = operationId.ToString(),
                        ["target_node_id"] = node.NodeId,
                        ["region_id"] = regionId,
                        ["action"] = action,
                        ["file_name"] = fileName,
                        ["dry_run"] = dryRun.ToString()
                    },
                    operationId.ToString()));

                WriteJson(response, HttpStatusCode.Accepted, record.Payload());
            }
        }

        private void ApplyState(NexEvent nexEvent)
        {
            if (nexEvent == null || string.IsNullOrWhiteSpace(nexEvent.Name)
                || !nexEvent.Name.StartsWith("archive.oar.operation.", StringComparison.OrdinalIgnoreCase))
                return;

            if (nexEvent.Data == null
                || !nexEvent.Data.TryGetValue("operation_id", out string raw)
                || !Guid.TryParse(raw, out Guid id)
                || !m_Operations.TryGetValue(id, out Record record))
                return;

            if (nexEvent.Data.TryGetValue("state", out string state) && !string.IsNullOrWhiteSpace(state))
                record.State = state.Trim().ToLowerInvariant();
            if (nexEvent.Data.TryGetValue("message", out string message))
                record.Message = message ?? string.Empty;
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }

        private bool Authenticate(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (m_Authenticator.TryAuthenticate(request, NexScopes.RegionsManage, out NexPrincipal _, out UserAccount _, out int status, out string error))
                return true;
            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteError(response, (HttpStatusCode)status, error, "Authentifizierung oder Berechtigung fehlgeschlagen.");
            return false;
        }

        private static string GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;

        private static void WriteJson(IOSHttpResponse response, HttpStatusCode status, object payload)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = body;
        }

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string error, string message) =>
            WriteJson(response, status, new { error, message });

        public void Dispose() => m_Subscription?.Dispose();

        private sealed class Record
        {
            public Guid Id { get; }
            public string RegionId { get; }
            public string NodeId { get; }
            public string Action { get; }
            public string FileName { get; }
            public bool DryRun { get; }
            public string State { get; set; } = "queued";
            public string Message { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
            public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

            public Record(Guid id, string regionId, string nodeId, string action, string fileName, bool dryRun)
            {
                Id = id; RegionId = regionId; NodeId = nodeId; Action = action; FileName = fileName; DryRun = dryRun;
            }

            public object Payload() => new
            {
                operation_id = Id,
                region_id = RegionId,
                node_id = NodeId,
                action = Action,
                file_name = FileName,
                dry_run = DryRun,
                state = State,
                message = Message,
                created_at = CreatedAt,
                updated_at = UpdatedAt
            };
        }
    }
}
