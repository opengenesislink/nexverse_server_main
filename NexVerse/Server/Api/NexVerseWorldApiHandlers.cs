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
                docs = m_PublicBaseUrl + "/api/v1/docs",
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
                ["/api/v1/docs"] = GetOperation("Self-hosted searchable API documentation and live explorer"),
                ["/api/v1/auth/session"] = CredentialPostOperation(
                    "Create a native NexVerse resident session"),
                ["/api/v1/auth/api-keys"] = AuthenticatedOperations(
                    ("get", "List restricted machine API keys", "admin:*", "200"),
                    ("post", "Create a restricted scoped machine API key", "admin:*", "201"),
                    ("patch", "Enable or disable a machine API key", "admin:*", "200")),
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
                ["/api/v1/audit"] = AuthenticatedOperations(
                    ("get", "Query persistent administrative audit history", "admin:*", "200")),
                ["/api/v1/regions"] = AuthenticatedOperations(
                    ("get", "Search selectable home/start regions", "regions:read", "200")),
                ["/api/v1/users/me"] = AuthenticatedOperations(
                    ("get", "Read the authenticated resident account", null, "200")),
                ["/api/v1/users"] = AuthenticatedOperations(
                    ("get", "Search user accounts", "admin:*", "200"),
                    ("post", "Create and provision a user account", "admin:*", "201")),
                ["/api/v1/users/{principalId}"] = AuthenticatedOperations(
                    ("get", "Read a user account", "self or admin:*", "200"),
                    ("patch", "Update account profile fields", "self or admin:*", "200"),
                    ("delete", "Soft-delete/deactivate a user account", "admin:*", "200")),
                ["/api/v1/users/{principalId}/audit"] = AuthenticatedOperations(
                    ("get", "Read persistent audit history for one resident", "admin:*", "200")),
                ["/api/v1/users/{principalId}/state"] = AuthenticatedOperations(
                    ("patch", "Lock, ban, deactivate or reactivate a user account", "admin:*", "200")),
                ["/api/v1/users/{principalId}/level"] = AuthenticatedOperations(
                    ("patch", "Change UserLevel", "admin:*", "200")),
                ["/api/v1/users/{principalId}/password"] = AuthenticatedOperations(
                    ("post", "Set or reset a user password", "self or admin:*", "200"))
            };

            ApplyJsonContract(
                paths,
                "/api/v1/users",
                "get",
                null,
                "UserSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users",
                "post",
                "UserCreateRequest",
                "UserCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/regions",
                "get",
                null,
                "RegionSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/me",
                "get",
                null,
                "User",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}",
                "get",
                null,
                "User",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}",
                "patch",
                "UserUpdateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/state",
                "patch",
                "AccountStateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/level",
                "patch",
                "UserLevelRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/password",
                "post",
                "PasswordRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/session",
                "post",
                "ResidentSessionRequest",
                "ResidentSessionResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "get",
                null,
                "ApiKeyListResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "post",
                "ApiKeyCreateRequest",
                "ApiKeyCreateResponse",
                "201");
            ApplyJsonContract(
                paths,
                "/api/v1/auth/api-keys",
                "patch",
                "ApiKeyStateRequest",
                null,
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/audit",
                "get",
                null,
                "AuditSearchResponse",
                "200");
            ApplyJsonContract(
                paths,
                "/api/v1/users/{principalId}/audit",
                "get",
                null,
                "AuditSearchResponse",
                "200");

            object principalIdParameter =
                PathParameter(
                    "principalId",
                    "Resident principal UUID.");

            foreach (string method in new[]
                     {
                         "get",
                         "patch",
                         "delete"
                     })
            {
                AddOperationParameters(
                    paths,
                    "/api/v1/users/{principalId}",
                    method,
                    principalIdParameter);
            }

            foreach (string path in new[]
                     {
                         "/api/v1/users/{principalId}/audit",
                         "/api/v1/users/{principalId}/state",
                         "/api/v1/users/{principalId}/level",
                         "/api/v1/users/{principalId}/password"
                     })
            {
                string method =
                    path.EndsWith("/audit", StringComparison.Ordinal)
                        ? "get"
                        : path.EndsWith("/password", StringComparison.Ordinal)
                            ? "post"
                            : "patch";

                AddOperationParameters(
                    paths,
                    path,
                    method,
                    principalIdParameter);
            }

            AddOperationParameters(
                paths,
                "/api/v1/users",
                "get",
                QueryParameter(
                    "q",
                    true,
                    "User search query; minimum two characters."),
                QueryParameter(
                    "state",
                    false,
                    "Optional account-state filter."),
                QueryParameter(
                    "sort",
                    false,
                    "Sort by name, created, user_level or state."),
                QueryParameter(
                    "order",
                    false,
                    "Sort direction: asc or desc."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/users",
                "post",
                HeaderParameter(
                    "Idempotency-Key",
                    false,
                    "Optional retry-safe request key; maximum 128 characters."));

            AddOperationParameters(
                paths,
                "/api/v1/regions",
                "get",
                QueryParameter(
                    "q",
                    false,
                    "Optional region name or UUID query."),
                QueryParameter(
                    "sort",
                    false,
                    "Sort by name, size_x or size_y."),
                QueryParameter(
                    "order",
                    false,
                    "Sort direction: asc or desc."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/audit",
                "get",
                QueryParameter(
                    "resource",
                    false,
                    "Exact resource identifier."),
                QueryParameter(
                    "actor",
                    false,
                    "Exact audit actor."),
                QueryParameter(
                    "action",
                    false,
                    "Exact action or trailing-wildcard prefix such as users.*."),
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

            AddOperationParameters(
                paths,
                "/api/v1/users/{principalId}/audit",
                "get",
                PaginationParameter("limit", 50, 1, 100),
                PaginationParameter("offset", 0, 0, 10000));

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
                    schemas = BuildSchemas(),
                    securitySchemes = new
                    {
                        bearerAuth = new
                        {
                            type = "http",
                            scheme = "bearer",
                            bearerFormat = "JWT"
                        },
                        apiKeyAuth = new
                        {
                            type = "apiKey",
                            @in = "header",
                            name = "X-NexVerse-Api-Key"
                        }
                    }
                },
                x_nexverse_version_history = new object[]
                {
                    new
                    {
                        api_version = NexVersePlatform.ApiVersion,
                        server_line = "0.9.3.1 Dev",
                        codename = NexVersePlatform.MilestoneCodename,
                        status = "development",
                        published = "2026-10-01",
                        compatibility = "Initial NexVerse World API v1 contract",
                        highlights = new[]
                        {
                            "Native resident sessions and scoped access tokens",
                            "OAuth2/OIDC Authorization Code + PKCE and service clients",
                            "Persistent audit history, API keys and idempotent provisioning",
                            "Self-hosted API Control Center and live explorer",
                            "Distributed NexBus and NodeAgent foundation",
                            "Prometheus/OpenTelemetry/OTLP observability foundation"
                        }
                    }
                },
                x_nexverse_changelog = new object[]
                {
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "observability",
                        status = "implemented",
                        title = "Production OTLP observability pipeline",
                        summary = "Prometheus-compatible NexMetrics plus OpenTelemetry-compatible tracing and bounded OTLP/HTTP JSON export with retry and TLS controls.",
                        endpoints = new[] { "/internal/metrics" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "security",
                        status = "implemented",
                        title = "Native authentication cutover",
                        summary = "World API authentication no longer depends on the interim legacy AuthenticationService bearer-token bootstrap.",
                        endpoints = new[] { "/api/v1/auth/session", "/oauth/token", "/oauth/revoke", "/api/v1/auth/sessions/revoke" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "api",
                        status = "implemented",
                        title = "OAuth2/OIDC, API keys and persistent API safety controls",
                        summary = "Authorization Code + PKCE, refresh-token rotation, ES256 ID tokens/JWKS, service-account client credentials, hashed scoped API keys, rate limiting and persistent idempotency are available.",
                        endpoints = new[] { "/oauth/authorize", "/oauth/token", "/oauth/jwks", "/api/v1/auth/api-keys", "/api/v1/auth/clients" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "api",
                        status = "implemented",
                        title = "OpenAPI 3.1 contract and API Control Center",
                        summary = "The live contract exposes concrete schemas, endpoint audience/security metadata, version history and machine-readable release notes used by the self-hosted web interface.",
                        endpoints = new[] { "/api/v1/openapi.json", "/api/v1/docs" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "identity",
                        status = "implemented",
                        title = "Resident-compatible login names",
                        summary = "Short, dotted and legacy Resident login forms are normalized without rewriting stored account names.",
                        endpoints = Array.Empty<string>()
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "platform",
                        status = "implemented",
                        title = "Distributed NexBus and simulator NodeAgent foundation",
                        summary = "Authenticated HMAC peer transport, bounded delivery, event deduplication, node heartbeats and region lifecycle events connect Robust and simulator nodes.",
                        endpoints = new[] { "/internal/nexbus/v1/events" }
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "runtime",
                        status = "implemented",
                        title = "BulletSim and Meshmerizer runtime baseline restored",
                        summary = "The NexVerse simulator profile again standardizes BulletSim physics with Meshmerizer and keeps Warp3D map rendering as the supported runtime baseline.",
                        endpoints = Array.Empty<string>()
                    },
                    new
                    {
                        date = "2026-10-01",
                        version = "0.9.3.1 Dev",
                        category = "scripting",
                        status = "implemented",
                        title = "LSL/OSSL compatibility work",
                        summary = "RemoteData XML-RPC regression coverage, the simulator XML-RPC compatibility profile and additional OSSL helpers for scripted content are enabled and CI guarded.",
                        endpoints = Array.Empty<string>()
                    }
                },
                x_nexverse_idempotency = new
                {
                    header = "Idempotency-Key",
                    maximum_key_length = 128,
                    replay_header = "Idempotency-Replayed",
                    protected_operations = new[]
                    {
                        "POST /api/v1/users"
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
                x_nexverse_filtering_sorting = new
                {
                    users = new
                    {
                        filters = new[] { "state" },
                        sort = new[] { "name", "created", "user_level", "state" },
                        order = new[] { "asc", "desc" }
                    },
                    regions = new
                    {
                        sort = new[] { "name", "size_x", "size_y" },
                        order = new[] { "asc", "desc" }
                    }
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
            Dictionary<string, object> operation =
                new Dictionary<string, object>
                {
                    ["summary"] = summary,
                    ["responses"] =
                        new Dictionary<string, object>
                        {
                            ["200"] =
                                new
                                {
                                    description =
                                        "Successful response"
                                },
                            ["429"] =
                                JsonResponse(
                                    "Rate limit exceeded",
                                    "Error")
                        },
                    ["security"] = Array.Empty<object>(),
                    ["x-nexverse-audience"] =
                        new[]
                        {
                            "citizen",
                            "admin",
                            "service"
                        },
                    ["x-nexverse-purpose"] = summary,
                    ["x-nexverse-ai-instruction"] =
                        "This is a public read-only operation. Do not infer additional privileges from its availability.",
                    ["x-nexverse-security-constraints"] =
                        new[]
                        {
                            "Do not send credentials unless an operation explicitly requires them.",
                            "Respect rate limits and correlation IDs."
                        }
                };

            return new Dictionary<string, object>
            {
                ["get"] = operation
            };
        }

        private static object CredentialPostOperation(
            string summary)
        {
            Dictionary<string, object> operation =
                new Dictionary<string, object>
                {
                    ["summary"] = summary,
                    ["security"] = Array.Empty<object>(),
                    ["responses"] =
                        new Dictionary<string, object>
                        {
                            ["200"] =
                                new
                                {
                                    description =
                                        "Successful response"
                                },
                            ["400"] =
                                JsonResponse(
                                    "Invalid request",
                                    "Error"),
                            ["401"] =
                                JsonResponse(
                                    "Invalid resident credentials",
                                    "Error"),
                            ["429"] =
                                JsonResponse(
                                    "Rate limit exceeded",
                                    "Error"),
                            ["503"] =
                                JsonResponse(
                                    "Native token service unavailable",
                                    "Error")
                        },
                    ["x-nexverse-audience"] =
                        new[]
                        {
                            "citizen",
                            "admin"
                        },
                    ["x-nexverse-purpose"] = summary,
                    ["x-nexverse-ai-instruction"] =
                        "Use this endpoint only to create a resident session from credentials explicitly supplied for that purpose. Never infer, reuse, persist or expose a password.",
                    ["x-nexverse-security-constraints"] =
                        new[]
                        {
                            "TLS or an equivalent protected transport is mandatory.",
                            "Send the password only in the JSON request body.",
                            "Never log, cache or persist the password or returned access token."
                        }
                };

            return new Dictionary<string, object>
            {
                ["post"] = operation
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
                        },
                        new Dictionary<string, string[]>
                        {
                            ["apiKeyAuth"] = Array.Empty<string>()
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        [operationSpec.successCode] = new { description = "Successful response" },
                        ["400"] = JsonResponse("Invalid request", "Error"),
                        ["401"] = JsonResponse("Authentication required", "Error"),
                        ["403"] = JsonResponse("Insufficient scope", "Error"),
                        ["404"] = JsonResponse("Resource not found", "Error"),
                        ["429"] = JsonResponse("Rate limit exceeded", "Error")
                    }
                };

                if (!string.IsNullOrWhiteSpace(operationSpec.scope))
                    operation["x-nexverse-scope"] = operationSpec.scope;

                operation["x-nexverse-audience"] =
                    AudienceForScope(operationSpec.scope);
                operation["x-nexverse-purpose"] =
                    operationSpec.summary;
                operation["x-nexverse-ai-instruction"] =
                    AiInstructionForScope(operationSpec.scope);
                operation["x-nexverse-security-constraints"] =
                    SecurityConstraintsForScope(
                        operationSpec.scope);

                result[operationSpec.method] = operation;
            }

            return result;
        }

        private static string[] AudienceForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "admin" };
            }

            if (!string.IsNullOrWhiteSpace(scope) &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "admin",
                    "service"
                };
            }

            return new[]
            {
                "citizen",
                "admin"
            };
        }

        private static string AiInstructionForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                !scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Administrative operation. Require explicit authenticated admin authorization; never infer or escalate permissions.";
            }

            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.StartsWith(
                    "self",
                    StringComparison.OrdinalIgnoreCase))
            {
                return
                    "Resident self-service operation. Default to the authenticated subject; accessing another resident requires the documented admin authorization.";
            }

            if (!string.IsNullOrWhiteSpace(scope))
            {
                return
                    "Scoped operation. Verify that the authenticated principal has the documented scope before acting.";
            }

            return
                "Authenticated operation. Follow the documented authentication flow and never invent authorization.";
        }

        private static string[] SecurityConstraintsForScope(
            string scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) &&
                scope.IndexOf(
                    NexVerse.Core.Security.NexScopes.AdminAll,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new[]
                {
                    "Administrative authorization must be explicit.",
                    "Never expose bearer tokens, API keys, passwords or client secrets.",
                    "Preserve audit and correlation metadata for state-changing actions."
                };
            }

            return new[]
            {
                "Never expose bearer tokens, API keys, passwords or client secrets.",
                "Respect the documented scope, rate limits and idempotency requirements."
            };
        }

        private static void ApplyJsonContract(
            Dictionary<string, object> paths,
            string path,
            string method,
            string requestSchema,
            string responseSchema,
            string successCode)
        {
            if (!paths.TryGetValue(path, out object pathValue) ||
                pathValue is not Dictionary<string, object> pathItem ||
                !pathItem.TryGetValue(method, out object operationValue) ||
                operationValue is not Dictionary<string, object> operation)
                return;

            if (!string.IsNullOrWhiteSpace(requestSchema))
            {
                operation["requestBody"] = new
                {
                    required = true,
                    content = new Dictionary<string, object>
                    {
                        ["application/json"] = new
                        {
                            schema = SchemaRef(requestSchema)
                        }
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(responseSchema) &&
                operation.TryGetValue(
                    "responses",
                    out object responsesValue) &&
                responsesValue is Dictionary<string, object> responses)
            {
                responses[successCode] =
                    JsonResponse(
                        "Successful response",
                        responseSchema);
            }
        }

        private static void AddOperationParameters(
            Dictionary<string, object> paths,
            string path,
            string method,
            params object[] parameters)
        {
            if (!paths.TryGetValue(path, out object pathValue) ||
                pathValue is not Dictionary<string, object> pathItem ||
                !pathItem.TryGetValue(method, out object operationValue) ||
                operationValue is not Dictionary<string, object> operation)
                return;

            operation["parameters"] = parameters;
        }

        private static object PathParameter(
            string name,
            string description)
        {
            return new
            {
                name,
                @in = "path",
                required = true,
                description,
                schema = new
                {
                    type = "string",
                    format = "uuid"
                }
            };
        }

        private static object QueryParameter(
            string name,
            bool required,
            string description)
        {
            return new
            {
                name,
                @in = "query",
                required,
                description,
                schema = new
                {
                    type = "string"
                }
            };
        }

        private static object HeaderParameter(
            string name,
            bool required,
            string description)
        {
            return new
            {
                name,
                @in = "header",
                required,
                description,
                schema = new
                {
                    type = "string"
                }
            };
        }

        private static object PaginationParameter(
            string name,
            int defaultValue,
            int minimum,
            int maximum)
        {
            return new
            {
                name,
                @in = "query",
                required = false,
                schema = new
                {
                    type = "integer",
                    @default = defaultValue,
                    minimum,
                    maximum
                }
            };
        }

        private static object JsonResponse(
            string description,
            string schemaName)
        {
            return new
            {
                description,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = SchemaRef(schemaName)
                    }
                }
            };
        }

        private static Dictionary<string, object> SchemaRef(
            string schemaName)
        {
            return new Dictionary<string, object>
            {
                ["$ref"] =
                    "#/components/schemas/" + schemaName
            };
        }

        private static Dictionary<string, object> BuildSchemas()
        {
            object paginationRef = SchemaRef("Pagination");
            object userRef = SchemaRef("User");
            object regionRef = SchemaRef("Region");
            object apiKeyRef = SchemaRef("ApiKey");
            object auditEventRef = SchemaRef("AuditEvent");

            return new Dictionary<string, object>
            {
                ["Error"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "error",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["error"] = new { type = "string" },
                        ["message"] = new { type = "string" },
                        ["error_description"] = new { type = "string" },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["Pagination"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "limit",
                        "offset",
                        "returned",
                        "has_more"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["limit"] = new { type = "integer", minimum = 1, maximum = 100 },
                        ["offset"] = new { type = "integer", minimum = 0, maximum = 10000 },
                        ["returned"] = new { type = "integer", minimum = 0 },
                        ["has_more"] = new { type = "boolean" },
                        ["next_offset"] = new
                        {
                            type = new[] { "integer", "null" }
                        }
                    }
                },
                ["Position"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["x"] = new { type = "number", format = "float" },
                        ["y"] = new { type = "number", format = "float" },
                        ["z"] = new { type = "number", format = "float" }
                    }
                },
                ["Region"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "region_id",
                        "name",
                        "size_x",
                        "size_y"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["region_id"] = new { type = "string", format = "uuid" },
                        ["name"] = new { type = "string" },
                        ["server_uri"] = new { type = "string" },
                        ["size_x"] = new { type = "integer" },
                        ["size_y"] = new { type = "integer" }
                    }
                },
                ["User"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "principal_id",
                        "first_name",
                        "last_name",
                        "user_level",
                        "active",
                        "account_state"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["principal_id"] = new { type = "string", format = "uuid" },
                        ["first_name"] = new { type = "string" },
                        ["last_name"] = new { type = "string" },
                        ["email"] = new { type = "string" },
                        ["user_level"] = new { type = "integer" },
                        ["user_flags"] = new { type = "integer" },
                        ["user_title"] = new { type = "string" },
                        ["user_country"] = new { type = "string" },
                        ["local_to_grid"] = new { type = "boolean" },
                        ["active"] = new { type = "boolean" },
                        ["account_state"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "active",
                                "locked",
                                "banned",
                                "deactivated",
                                "provisioning",
                                "provisioning_failed"
                            }
                        },
                        ["account_state_reason"] = new { type = "string" },
                        ["account_state_changed"] = new { type = "integer" },
                        ["created"] = new { type = "integer" }
                    }
                },
                ["UserCreateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "first_name",
                        "last_name",
                        "password",
                        "home_region"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["first_name"] = new { type = "string", maxLength = 64 },
                        ["last_name"] = new { type = "string", maxLength = 64 },
                        ["email"] = new { type = "string", maxLength = 64 },
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 8,
                            maxLength = 256,
                            writeOnly = true
                        },
                        ["home_region"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["id"] = new { type = "string", format = "uuid" },
                                ["name"] = new { type = "string" },
                                ["position"] = SchemaRef("Position")
                            }
                        }
                    }
                },
                ["UserCreateResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "user",
                        "provisioning",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["user"] = userRef,
                        ["provisioning"] = new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>
                            {
                                ["ready"] = new { type = "boolean" },
                                ["authentication_initialized"] = new { type = "boolean" },
                                ["inventory_initialized"] = new { type = "boolean" },
                                ["home_initialized"] = new { type = "boolean" },
                                ["start_position_initialized"] = new { type = "boolean" },
                                ["state_finalized"] = new { type = "boolean" },
                                ["account_state"] = new { type = "string" },
                                ["home_region"] = regionRef
                            }
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["UserSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["users"] = new
                        {
                            type = "array",
                            items = userRef
                        },
                        ["pagination"] = paginationRef
                    }
                },
                ["RegionSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["regions"] = new
                        {
                            type = "array",
                            items = regionRef
                        },
                        ["pagination"] = paginationRef
                    }
                },
                ["UserUpdateRequest"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["email"] = new { type = "string", maxLength = 64 },
                        ["user_title"] = new { type = "string", maxLength = 64 },
                        ["user_country"] = new { type = "string", maxLength = 64 }
                    }
                },
                ["AccountStateRequest"] = new
                {
                    type = "object",
                    required = new[] { "state" },
                    properties = new Dictionary<string, object>
                    {
                        ["state"] = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "active",
                                "locked",
                                "banned",
                                "deactivated"
                            }
                        },
                        ["reason"] = new { type = "string", maxLength = 255 }
                    }
                },
                ["UserLevelRequest"] = new
                {
                    type = "object",
                    required = new[] { "user_level" },
                    properties = new Dictionary<string, object>
                    {
                        ["user_level"] = new { type = "integer" }
                    }
                },
                ["ResidentSessionRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "username",
                        "password"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["username"] = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 129,
                            description = "Canonical NexVerse resident login name, for example Antonia.Porta or a single-name Resident login."
                        },
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 256,
                            writeOnly = true
                        }
                    }
                },
                ["ResidentSessionResponse"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "access_token",
                        "token_type",
                        "expires_in",
                        "scope",
                        "principal_id",
                        "correlation_id"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["access_token"] = new
                        {
                            type = "string",
                            description = "Native lifecycle-bound NexVerse bearer token."
                        },
                        ["token_type"] = new
                        {
                            type = "string",
                            @enum = new[] { "Bearer" }
                        },
                        ["expires_in"] = new { type = "integer" },
                        ["scope"] = new { type = "string" },
                        ["principal_id"] = new
                        {
                            type = "string",
                            format = "uuid"
                        },
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["PasswordRequest"] = new
                {
                    type = "object",
                    required = new[] { "password" },
                    properties = new Dictionary<string, object>
                    {
                        ["password"] = new
                        {
                            type = "string",
                            minLength = 8,
                            maxLength = 256,
                            writeOnly = true
                        }
                    }
                },
                ["ApiKey"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["key_id"] = new { type = "string" },
                        ["name"] = new { type = "string" },
                        ["scopes"] = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        ["enabled"] = new { type = "boolean" },
                        ["created_at"] = new { type = "integer" },
                        ["updated_at"] = new { type = "integer" }
                    }
                },
                ["ApiKeyCreateRequest"] = new
                {
                    type = "object",
                    required = new[] { "scopes" },
                    properties = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "string" },
                        ["scopes"] = new
                        {
                            type = "array",
                            minItems = 1,
                            items = new { type = "string" }
                        }
                    }
                },
                ["ApiKeyCreateResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["api_key"] = new
                        {
                            type = "string",
                            writeOnly = true
                        },
                        ["api_key_note"] = new { type = "string" },
                        ["key"] = apiKeyRef,
                        ["correlation_id"] = new { type = "string" }
                    }
                },
                ["ApiKeyStateRequest"] = new
                {
                    type = "object",
                    required = new[]
                    {
                        "key_id",
                        "enabled"
                    },
                    properties = new Dictionary<string, object>
                    {
                        ["key_id"] = new { type = "string" },
                        ["enabled"] = new { type = "boolean" }
                    }
                },
                ["ApiKeyListResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["keys"] = new
                        {
                            type = "array",
                            items = apiKeyRef
                        }
                    }
                },
                ["AuditEvent"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["event_id"] = new { type = "string", format = "uuid" },
                        ["timestamp"] = new { type = "string", format = "date-time" },
                        ["actor"] = new { type = "string" },
                        ["action"] = new { type = "string" },
                        ["resource"] = new { type = "string" },
                        ["correlation_id"] = new { type = "string" },
                        ["details"] = new
                        {
                            type = "object",
                            additionalProperties = new
                            {
                                type = "string"
                            }
                        }
                    }
                },
                ["AuditSearchResponse"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer" },
                        ["events"] = new
                        {
                            type = "array",
                            items = auditEventRef
                        },
                        ["pagination"] = paginationRef
                    }
                }
            };
        }

        private static void WriteJson(IOSHttpResponse response, object payload)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_JsonOptions);
        }
    }
}
