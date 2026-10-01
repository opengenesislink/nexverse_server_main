// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using NexVerse.Core;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using NexVerse.Core.Observability;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    public sealed class NexVerseWorldApiHandlers
    {
        private static readonly JsonSerializerOptions s_JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string m_PublicBaseUrl;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_AuditSink;

        public NexVerseWorldApiHandlers(
            string publicBaseUrl,
            INexEventBus eventBus,
            INexAuditSink auditSink)
        {
            m_PublicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
                ? "http://world.stadt-nexverse.de"
                : publicBaseUrl.TrimEnd('/');

            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            m_AuditSink = auditSink ?? NullNexAuditSink.Instance;
        }

        public void Root(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.root", "/api/v1");

            WriteJson(response, new
            {
                service = "NexVerse World API",
                product = NexVersePlatform.ProductName,
                api_version = NexVersePlatform.ApiVersion,
                milestone = NexVersePlatform.MilestoneCodename,
                status = "development",
                health = m_PublicBaseUrl + "/api/v1/health",
                version = m_PublicBaseUrl + "/api/v1/version",
                capabilities = m_PublicBaseUrl + "/api/v1/capabilities",
                openapi = m_PublicBaseUrl + "/api/v1/openapi.json",
                correlation_id = correlationId
            });
        }

        public void Health(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.health.read", "/api/v1/health");

            WriteJson(response, new
            {
                status = "ok",
                product = NexVersePlatform.ProductName,
                api_version = NexVersePlatform.ApiVersion,
                milestone = NexVersePlatform.MilestoneCodename,
                timestamp = DateTimeOffset.UtcNow,
                correlation_id = correlationId
            });
        }

        public void Version(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.version.read", "/api/v1/version");

            WriteJson(response, new
            {
                product = NexVersePlatform.ProductName,
                server_version = OpenSim.VersionInfo.Version.Trim(),
                api_version = NexVersePlatform.ApiVersion,
                protocol_version = NexVersePlatform.ProtocolVersion,
                nexbus_schema_version = NexVersePlatform.NexBusSchemaVersion,
                milestone = NexVersePlatform.MilestoneCodename,
                correlation_id = correlationId
            });
        }

        public void Capabilities(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            string correlationId = BeginRequest(response, "worldapi.capabilities.read", "/api/v1/capabilities");

            WriteJson(response, new
            {
                product = NexVersePlatform.ProductName,
                capabilities = NexVersePlatform.GetCompatibilityLevels(),
                correlation_id = correlationId
            });
        }

        public void OpenApi(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireGet(request, response))
                return;

            BeginRequest(response, "worldapi.openapi.read", "/api/v1/openapi.json");

            Dictionary<string, object> paths = new Dictionary<string, object>
            {
                ["/api/v1"] = GetOperation("World API service metadata"),
                ["/api/v1/health"] = GetOperation("World API health"),
                ["/api/v1/version"] = GetOperation("NexVerse server and protocol versions"),
                ["/api/v1/capabilities"] = GetOperation("NexVerse capability and compatibility levels"),
                ["/api/v1/openapi.json"] = GetOperation("OpenAPI document"),
                ["/api/v1/auth/token"] = AuthenticatedOperations(
                    ("post", "Exchange a legacy AuthenticationService token for a NexVerse scoped access token", null, "200")),
                ["/oauth/authorize"] = AuthenticatedOperations(
                    ("get", "OAuth 2.0 Authorization Code + PKCE authorization endpoint", null, "302")),
                ["/oauth/token"] = AuthenticatedOperations(
                    ("post", "OAuth 2.0 token endpoint: authorization_code, refresh_token and client_credentials", null, "200")),
                ["/oauth/revoke"] = AuthenticatedOperations(
                    ("post", "OAuth 2.0 token revocation endpoint", null, "200")),
                ["/api/v1/auth/clients"] = AuthenticatedOperations(
                    ("get", "List OAuth/service clients", "admin:*", "200"),
                    ("post", "Register OAuth/service client", "admin:*", "201"),
                    ("patch", "Enable or disable OAuth/service client", "admin:*", "200")),
                ["/api/v1/auth/sessions/revoke"] = AuthenticatedOperations(
                    ("post", "Revoke resident sessions and advance security stamp", "self or admin:*", "200")),
                ["/api/v1/regions"] = AuthenticatedOperations(
                    ("get", "Search selectable home/start regions", "admin:*", "200")),
                ["/api/v1/users/me"] = AuthenticatedOperations(
                    ("get", "Read the authenticated resident account", null, "200")),
                ["/api/v1/users"] = AuthenticatedOperations(
                    ("get", "Search user accounts", "admin:*", "200"),
                    ("post", "Create and provision a user account", "admin:*", "201")),
                ["/api/v1/users/{principalId}"] = AuthenticatedOperations(
                    ("get", "Read a user account", "self or admin:*", "200"),
                    ("patch", "Update account profile fields", "self or admin:*", "200"),
                    ("delete", "Soft-delete/deactivate a user account", "admin:*", "200")),
                ["/api/v1/users/{principalId}/state"] = AuthenticatedOperations(
                    ("patch", "Lock, ban, deactivate or reactivate a user account", "admin:*", "200")),
                ["/api/v1/users/{principalId}/level"] = AuthenticatedOperations(
                    ("patch", "Change UserLevel", "admin:*", "200")),
                ["/api/v1/users/{principalId}/password"] = AuthenticatedOperations(
                    ("post", "Set or reset a user password", "self or admin:*", "200"))
            };

            WriteJson(response, new
            {
                openapi = "3.1.0",
                info = new
                {
                    title = "NexVerse World API",
                    version = NexVersePlatform.ApiVersion,
                    description = "NexVerse Robust control-plane API. Administrative endpoints will require scoped authentication."
                },
                servers = new[]
                {
                    new { url = m_PublicBaseUrl }
                },
                paths,
                components = new
                {
                    securitySchemes = new
                    {
                        bearerAuth = new
                        {
                            type = "http",
                            scheme = "bearer",
                            bearerFormat = "JWT"
                        }
                    }
                },
                x_nexverse_rate_limit = new
                {
                    headers = new[]
                    {
                        "RateLimit-Limit",
                        "RateLimit-Remaining",
                        "RateLimit-Reset",
                        "Retry-After"
                    },
                    status = 429
                },
                x_nexverse_pagination = new
                {
                    query_parameters = new[] { "limit", "offset" },
                    default_limit = 50,
                    maximum_limit = 100,
                    maximum_offset = 10000,
                    response_fields = new[]
                    {
                        "limit",
                        "offset",
                        "returned",
                        "has_more",
                        "next_offset"
                    }
                },
                x_nexverse_audience = new[] { "citizen", "admin" },
                x_nexverse_ai_instruction = new
                {
                    citizen = "Use only citizen-authorized endpoints and never assume administrative permissions.",
                    admin = "Administrative actions require explicit authenticated scopes and must produce audit events."
                }
            });
        }

        private string BeginRequest(IOSHttpResponse response, string action, string resource)
        {
            string correlationId =
                NexApiRequestContext.Ensure(response);

            NexMetricsRegistry.Default.IncrementCounter(
                "nexverse_world_api_public_requests_total",
                "Public NexVerse World API requests.",
                1,
                new Dictionary<string, string>
                {
                    ["route"] = resource
                });

            response.KeepAlive = false;

            m_AuditSink.Record(new NexAuditEvent(
                "anonymous",
                action,
                resource,
                correlationId));

            m_EventBus.Publish(new NexEvent(
                "api.request",
                "nexverse.world-api",
                new Dictionary<string, string>
                {
                    ["action"] = action,
                    ["resource"] = resource
                },
                correlationId));

            return correlationId;
        }

        private static bool RequireGet(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request != null && string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                return true;

            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType = "application/json";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = "method_not_allowed",
                message = "This endpoint currently accepts GET requests only.",
                correlation_id = NexApiRequestContext.CurrentCorrelationId
            }, s_JsonOptions);
            return false;
        }

        private static object GetOperation(string summary)
        {
            return new
            {
                get = new
                {
                    summary,
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new { description = "Successful response" },
                        ["429"] = new { description = "Rate limit exceeded" }
                    },
                    security = Array.Empty<object>()
                }
            };
        }

        private static object AuthenticatedOperations(
            params (string method, string summary, string scope, string successCode)[] operations)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            foreach ((string method, string summary, string scope, string successCode) operationSpec in operations)
            {
                Dictionary<string, object> operation = new Dictionary<string, object>
                {
                    ["summary"] = operationSpec.summary,
                    ["security"] = new object[]
                    {
                        new Dictionary<string, string[]>
                        {
                            ["bearerAuth"] = Array.Empty<string>()
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        [operationSpec.successCode] = new { description = "Successful response" },
                        ["400"] = new { description = "Invalid request" },
                        ["401"] = new { description = "Authentication required" },
                        ["403"] = new { description = "Insufficient scope" },
                        ["404"] = new { description = "Resource not found" },
                        ["429"] = new { description = "Rate limit exceeded" }
                    }
                };

                if (!string.IsNullOrWhiteSpace(operationSpec.scope))
                    operation["x-nexverse-scope"] = operationSpec.scope;

                result[operationSpec.method] = operation;
            }

            return result;
        }

        private static void WriteJson(IOSHttpResponse response, object payload)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_JsonOptions);
        }
    }
}
