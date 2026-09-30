// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Identity;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexApiAuthenticator
    {
        private readonly IAuthenticationService m_Authentication;
        private readonly IUserAccountService m_UserAccounts;
        private readonly INexAuthorizationService m_Authorization;
        private readonly int m_AdminMinimumLevel;
        private readonly int m_TokenLifetimeSeconds;

        public NexApiAuthenticator(
            IAuthenticationService authentication,
            IUserAccountService userAccounts,
            INexAuthorizationService authorization,
            int adminMinimumLevel,
            int tokenLifetimeSeconds)
        {
            m_Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
            m_Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
            m_AdminMinimumLevel = adminMinimumLevel;
            m_TokenLifetimeSeconds = Math.Max(60, tokenLifetimeSeconds);
        }

        public bool TryAuthenticate(
            IOSHttpRequest request,
            string requiredScope,
            out NexPrincipal principal,
            out UserAccount account,
            out int statusCode,
            out string error)
        {
            principal = NexPrincipal.Anonymous;
            account = null;
            statusCode = (int)HttpStatusCode.Unauthorized;
            error = "authentication_required";

            string authorization = request?.Headers?["Authorization"];
            string principalHeader = request?.Headers?["X-NexVerse-Principal"];

            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(principalHeader))
                return false;

            string token = authorization.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrWhiteSpace(token) || !UUID.TryParse(principalHeader, out UUID principalId))
            {
                error = "invalid_authentication";
                return false;
            }

            if (!m_Authentication.Verify(principalId, token, m_TokenLifetimeSeconds))
            {
                error = "invalid_or_expired_token";
                return false;
            }

            account = m_UserAccounts.GetUserAccount(UUID.Zero, principalId);
            if (account == null)
            {
                error = "account_not_found";
                return false;
            }

            List<string> scopes = new List<string>();
            if (account.UserLevel >= m_AdminMinimumLevel)
                scopes.Add(NexScopes.AdminAll);

            principal = new NexPrincipal(principalId.ToString(), scopes, true);

            if (!string.IsNullOrWhiteSpace(requiredScope) &&
                !m_Authorization.IsAllowed(principal, requiredScope))
            {
                statusCode = (int)HttpStatusCode.Forbidden;
                error = "insufficient_scope";
                return false;
            }

            statusCode = (int)HttpStatusCode.OK;
            error = string.Empty;
            return true;
        }
    }

    internal sealed class OpenSimNexUserService : INexUserService
    {
        private readonly IUserAccountService m_UserAccounts;

        public OpenSimNexUserService(IUserAccountService userAccounts)
        {
            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
        }

        public NexUserRecord GetById(string principalId)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return null;

            return Convert(m_UserAccounts.GetUserAccount(UUID.Zero, id));
        }

        public IReadOnlyList<NexUserRecord> Search(string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<NexUserRecord>();

            int safeLimit = Math.Max(1, Math.Min(limit, 100));
            List<UserAccount> accounts = m_UserAccounts.GetUserAccounts(UUID.Zero, query.Trim());
            if (accounts == null)
                return Array.Empty<NexUserRecord>();

            return accounts
                .Take(safeLimit)
                .Select(Convert)
                .Where(x => x != null)
                .ToList();
        }

        public bool SetUserLevel(string principalId, int userLevel)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return false;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return false;

            account.UserLevel = userLevel;
            return m_UserAccounts.StoreUserAccount(account);
        }

        private static NexUserRecord Convert(UserAccount account)
        {
            if (account == null)
                return null;

            return new NexUserRecord(
                account.PrincipalID.ToString(),
                account.FirstName,
                account.LastName,
                account.Email,
                account.UserLevel,
                account.UserFlags,
                account.UserTitle,
                account.UserCountry,
                account.LocalToGrid,
                account.Created);
        }
    }

    internal sealed class NexUserApiRouter
    {
        private static readonly JsonSerializerOptions s_Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly INexUserService m_Users;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_Audit;

        public NexUserApiRouter(
            INexUserService users,
            NexApiAuthenticator authenticator,
            INexEventBus eventBus,
            INexAuditSink audit)
        {
            m_Users = users ?? throw new ArgumentNullException(nameof(users));
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            m_Audit = audit ?? NullNexAuditSink.Instance;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');

            if (string.Equals(path, "/api/v1/users/me", StringComparison.OrdinalIgnoreCase))
            {
                HandleMe(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/users", StringComparison.OrdinalIgnoreCase))
            {
                HandleSearch(request, response);
                return;
            }

            const string prefix = "/api/v1/users/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string tail = path.Substring(prefix.Length);
                string[] parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 1)
                {
                    HandleGet(request, response, parts[0]);
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "level", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSetLevel(request, response, parts[0]);
                    return;
                }
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown NexVerse API endpoint.");
        }

        private void HandleMe(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount _))
                return;

            NexUserRecord user = m_Users.GetById(principal.Subject);
            if (user == null)
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found.");
                return;
            }

            WriteUser(response, user);
        }

        private void HandleSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal _, out UserAccount _))
                return;

            string query = request.QueryString?["q"];
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_query", "Query parameter q must contain at least two characters.");
                return;
            }

            IReadOnlyList<NexUserRecord> users = m_Users.Search(query, 50);
            WriteJson(response, new
            {
                count = users.Count,
                users
            });
        }

        private void HandleGet(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount _))
                return;

            bool self = string.Equals(principal.Subject, principalId, StringComparison.OrdinalIgnoreCase);
            if (!self && !principal.HasScope(NexScopes.AdminAll))
            {
                WriteError(response, HttpStatusCode.Forbidden, "insufficient_scope", "Access to another user requires administrative scope.");
                return;
            }

            NexUserRecord user = m_Users.GetById(principalId);
            if (user == null)
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found.");
                return;
            }

            WriteUser(response, user);
        }

        private void HandleSetLevel(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "PATCH"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out UserAccount _))
                return;

            int level;
            try
            {
                using StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8, true, 1024, true);
                string body = reader.ReadToEnd();
                using JsonDocument document = JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty("user_level", out JsonElement levelElement) ||
                    !levelElement.TryGetInt32(out level) ||
                    level < -1 ||
                    level > 255)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_user_level", "user_level must be an integer between -1 and 255.");
                    return;
                }
            }
            catch (Exception)
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "Request body must contain valid JSON.");
                return;
            }

            if (!m_Users.SetUserLevel(principalId, level))
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found or could not be updated.");
                return;
            }

            string correlationId = AddCorrelation(response);
            m_Audit.Record(new NexAuditEvent(principal.Subject, "users.level.update", principalId, correlationId,
                new Dictionary<string, string> { ["user_level"] = level.ToString() }));

            m_EventBus.Publish(new NexEvent("user.level.changed", "nexverse.world-api",
                new Dictionary<string, string>
                {
                    ["principal_id"] = principalId,
                    ["user_level"] = level.ToString()
                },
                correlationId));

            WriteJson(response, new
            {
                principal_id = principalId,
                user_level = level,
                updated = true,
                correlation_id = correlationId
            });
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string requiredScope,
            out NexPrincipal principal,
            out UserAccount account)
        {
            if (m_Authenticator.TryAuthenticate(
                request,
                requiredScope,
                out principal,
                out account,
                out int statusCode,
                out string error))
                return true;

            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteError(response, (HttpStatusCode)statusCode, error, "Authentication or authorization failed.");
            return false;
        }

        private static bool RequireMethod(IOSHttpRequest request, IOSHttpResponse response, string method)
        {
            if (request != null && string.Equals(request.HttpMethod, method, StringComparison.OrdinalIgnoreCase))
                return true;

            WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "HTTP method is not allowed for this endpoint.");
            return false;
        }

        private static void WriteUser(IOSHttpResponse response, NexUserRecord user)
        {
            WriteJson(response, new
            {
                principal_id = user.PrincipalId,
                first_name = user.FirstName,
                last_name = user.LastName,
                email = user.Email,
                user_level = user.UserLevel,
                user_flags = user.UserFlags,
                user_title = user.UserTitle,
                user_country = user.UserCountry,
                local_to_grid = user.LocalToGrid,
                created = user.Created
            });
        }

        private static string AddCorrelation(IOSHttpResponse response)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            response.AddHeader("X-NexVerse-Api-Version", Core.NexVersePlatform.ApiVersion);
            response.AddHeader("X-Correlation-Id", correlationId);
            return correlationId;
        }

        private static void WriteJson(IOSHttpResponse response, object payload)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_Json);
        }

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string error, string message)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new { error, message }, s_Json);
        }
    }
}
