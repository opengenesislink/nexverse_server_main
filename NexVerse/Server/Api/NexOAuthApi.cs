// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexOAuthApiRouter
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly string m_PublicBaseUrl;
        private readonly INexAccessTokenService m_Tokens;
        private readonly INexOidcSigningService m_Oidc;
        private readonly INexOAuthStore m_Store;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IUserAccountService m_UserAccounts;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_Audit;
        private readonly int m_AuthorizationCodeLifetimeSeconds;
        private readonly int m_RefreshLifetimeSeconds;

        public NexOAuthApiRouter(
            string publicBaseUrl,
            INexAccessTokenService tokens,
            INexOidcSigningService oidc,
            INexOAuthStore store,
            NexApiAuthenticator authenticator,
            IUserAccountService userAccounts,
            INexEventBus eventBus,
            INexAuditSink audit,
            int authorizationCodeLifetimeSeconds,
            int refreshLifetimeSeconds)
        {
            m_PublicBaseUrl = (publicBaseUrl ?? string.Empty).TrimEnd('/');
            m_Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            m_Oidc = oidc ?? throw new ArgumentNullException(nameof(oidc));
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_AuthorizationCodeLifetimeSeconds = Math.Max(60, authorizationCodeLifetimeSeconds);
            m_RefreshLifetimeSeconds = Math.Max(300, refreshLifetimeSeconds);
        }

        public void Discovery(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            WriteJson(response, new
            {
                issuer = m_Oidc.Issuer,
                authorization_endpoint = m_PublicBaseUrl + "/oauth/authorize",
                token_endpoint = m_PublicBaseUrl + "/oauth/token",
                revocation_endpoint = m_PublicBaseUrl + "/oauth/revoke",
                jwks_uri = m_PublicBaseUrl + "/oauth/jwks",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "ES256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "none" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[]
                {
                    NexScopes.OpenId,
                    NexScopes.Profile,
                    NexScopes.OfflineAccess,
                    NexScopes.UsersRead,
                    NexScopes.UsersWrite,
                    NexScopes.InventoryRead,
                    NexScopes.InventoryWrite,
                    NexScopes.FriendsManage,
                    NexScopes.RegionsRead,
                    NexScopes.RegionsManage,
                    NexScopes.EstatesManage,
                    NexScopes.EconomyRead,
                    NexScopes.EconomyTransfer,
                    NexScopes.AdminAll
                }
            });
        }

        public void Jwks(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            response.KeepAlive = false;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = Encoding.UTF8.GetBytes(m_Oidc.GetJwksJson());
        }

        public void Authorize(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount account))
                return;

            if (account == null || !UUID.TryParse(principal.Subject, out _))
            {
                WriteOAuthError(response, HttpStatusCode.Forbidden, "access_denied", "Interactive authorization requires a resident account.");
                return;
            }

            string responseType = request.QueryString?["response_type"] ?? string.Empty;
            string clientId = request.QueryString?["client_id"] ?? string.Empty;
            string redirectUri = request.QueryString?["redirect_uri"] ?? string.Empty;
            string scopeRaw = request.QueryString?["scope"] ?? string.Empty;
            string state = request.QueryString?["state"] ?? string.Empty;
            string nonce = request.QueryString?["nonce"] ?? string.Empty;
            string challenge = request.QueryString?["code_challenge"] ?? string.Empty;
            string challengeMethod = request.QueryString?["code_challenge_method"] ?? string.Empty;

            if (responseType != "code" ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(redirectUri) ||
                string.IsNullOrWhiteSpace(challenge) ||
                !string.Equals(challengeMethod, "S256", StringComparison.Ordinal))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_request", "response_type=code and PKCE S256 are required.");
                return;
            }

            string[] scopes = SplitScopes(scopeRaw);
            foreach (string scope in scopes)
            {
                if (IsIdentityScope(scope))
                    continue;

                if (!principal.HasScope(scope))
                {
                    WriteOAuthError(response, HttpStatusCode.Forbidden, "invalid_scope", "The resident is not authorized for a requested scope.");
                    return;
                }
            }

            try
            {
                string code = m_Store.CreateAuthorizationCode(
                    clientId,
                    principal.Subject,
                    redirectUri,
                    scopes,
                    challenge,
                    nonce,
                    account.NexVerseStateChanged,
                    m_AuthorizationCodeLifetimeSeconds);

                string separator = redirectUri.Contains('?') ? "&" : "?";
                string location = redirectUri + separator + "code=" + WebUtility.UrlEncode(code);
                if (!string.IsNullOrEmpty(state))
                    location += "&state=" + WebUtility.UrlEncode(state);

                response.KeepAlive = false;
                response.StatusCode = (int)HttpStatusCode.Redirect;
                response.AddHeader("Location", location);
                response.RawBuffer = Array.Empty<byte>();
            }
            catch (Exception e)
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_request", e.Message);
            }
        }

        public void Token(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "POST"))
                return;

            Dictionary<string, string> form = ReadForm(request);
            if (!form.TryGetValue("grant_type", out string grantType))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_request", "grant_type is required.");
                return;
            }

            switch (grantType)
            {
                case "authorization_code":
                    HandleAuthorizationCode(request, response, form);
                    return;
                case "refresh_token":
                    HandleRefreshToken(request, response, form);
                    return;
                case "client_credentials":
                    HandleClientCredentials(request, response, form);
                    return;
                default:
                    WriteOAuthError(response, HttpStatusCode.BadRequest, "unsupported_grant_type", "Unsupported grant_type.");
                    return;
            }
        }

        public void Revoke(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "POST"))
                return;

            Dictionary<string, string> form = ReadForm(request);
            if (!form.TryGetValue("token", out string token) || string.IsNullOrWhiteSpace(token))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_request", "token is required.");
                return;
            }

            if (m_Tokens.TryValidate(token, out NexAccessTokenClaims claims))
                m_Store.RevokeAccessToken(claims.TokenId, claims.ExpiresAt);
            else
                m_Store.RevokeRefreshToken(token);

            WriteJson(response, new { revoked = true });
        }

        public void Clients(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out UserAccount _))
                return;

            if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(response, new
                {
                    clients = m_Store.ListClients().Select(ClientPayload).ToArray()
                });
                return;
            }

            if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadJson(request, response, out JsonDocument document))
                    return;

                using (document)
                {
                    JsonElement root = document.RootElement;
                    string name = GetString(root, "name");
                    string clientType = GetString(root, "client_type");
                    string[] redirectUris = GetStringArray(root, "redirect_uris");
                    string[] scopes = GetStringArray(root, "allowed_scopes");

                    try
                    {
                        NexOAuthClientRegistration registration = m_Store.CreateClient(
                            name,
                            clientType,
                            redirectUris,
                            scopes);

                        string correlationId = Correlation(response);
                        m_Audit.Record(new NexAuditEvent(
                            principal.Subject,
                            "auth.client.create",
                            registration.Client.ClientId,
                            correlationId,
                            new Dictionary<string, string>
                            {
                                ["client_type"] = registration.Client.ClientType
                            }));

                        m_EventBus.Publish(new NexEvent(
                            "auth.client.created",
                            "nexverse.world-api",
                            new Dictionary<string, string>
                            {
                                ["client_id"] = registration.Client.ClientId,
                                ["client_type"] = registration.Client.ClientType
                            },
                            correlationId));

                        WriteJson(response, new
                        {
                            client = ClientPayload(registration.Client),
                            client_secret = registration.ClientSecret,
                            client_secret_note = string.IsNullOrEmpty(registration.ClientSecret)
                                ? "public client: no client secret"
                                : "shown once; store securely",
                            correlation_id = correlationId
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_client_metadata", e.Message);
                    }
                }
                return;
            }

            if (string.Equals(request.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadJson(request, response, out JsonDocument document))
                    return;

                using (document)
                {
                    string clientId = GetString(document.RootElement, "client_id");
                    if (string.IsNullOrWhiteSpace(clientId) ||
                        !document.RootElement.TryGetProperty("enabled", out JsonElement enabledElement) ||
                        (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False))
                    {
                        WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_request", "client_id and boolean enabled are required.");
                        return;
                    }

                    bool enabled = enabledElement.GetBoolean();
                    if (!m_Store.SetClientEnabled(clientId, enabled))
                    {
                        WriteOAuthError(response, HttpStatusCode.NotFound, "client_not_found", "OAuth client was not found.");
                        return;
                    }

                    string correlationId = Correlation(response);
                    m_Audit.Record(new NexAuditEvent(
                        principal.Subject,
                        "auth.client.state.update",
                        clientId,
                        correlationId,
                        new Dictionary<string, string> { ["enabled"] = enabled.ToString() }));

                    WriteJson(response, new
                    {
                        client = ClientPayload(m_Store.GetClient(clientId)),
                        correlation_id = correlationId
                    });
                }
                return;
            }

            WriteOAuthError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "GET, POST or PATCH is required.");
        }

        public void RevokeSessions(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "POST"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount _))
                return;

            string subject = principal.Subject;
            if (TryReadOptionalJson(request, out JsonDocument document))
            {
                using (document)
                {
                    string requested = GetString(document.RootElement, "subject");
                    if (!string.IsNullOrWhiteSpace(requested))
                        subject = requested;
                }
            }

            bool self = string.Equals(subject, principal.Subject, StringComparison.OrdinalIgnoreCase);
            if (!self && !principal.HasScope(NexScopes.AdminAll))
            {
                WriteOAuthError(response, HttpStatusCode.Forbidden, "insufficient_scope", "Revoking another resident requires admin:*.");
                return;
            }

            if (!UUID.TryParse(subject, out UUID id))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_subject", "Session revocation currently applies to resident UUID subjects.");
                return;
            }

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
            {
                WriteOAuthError(response, HttpStatusCode.NotFound, "account_not_found", "Resident account was not found.");
                return;
            }

            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);

            if (!m_UserAccounts.StoreUserAccount(account))
            {
                WriteOAuthError(response, HttpStatusCode.InternalServerError, "session_revoke_failed", "Account security stamp could not be updated.");
                return;
            }

            m_UserAccounts.InvalidateCache(id);
            int refreshTokens = m_Store.RevokeSubjectRefreshTokens(subject);

            string correlationId = Correlation(response);
            m_Audit.Record(new NexAuditEvent(
                principal.Subject,
                "auth.sessions.revoke",
                subject,
                correlationId,
                new Dictionary<string, string>
                {
                    ["refresh_tokens"] = refreshTokens.ToString()
                }));

            m_EventBus.Publish(new NexEvent(
                "auth.sessions.revoked",
                "nexverse.world-api",
                new Dictionary<string, string>
                {
                    ["principal_id"] = subject
                },
                correlationId));

            WriteJson(response, new
            {
                subject,
                refresh_tokens_revoked = refreshTokens,
                access_tokens_invalidated = true,
                correlation_id = correlationId
            });
        }

        private void HandleAuthorizationCode(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Dictionary<string, string> form)
        {
            if (!TryGetClient(request, form, out NexOAuthClient client, out string clientId))
            {
                WriteOAuthError(response, HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");
                return;
            }

            if (!form.TryGetValue("code", out string code) ||
                !form.TryGetValue("redirect_uri", out string redirectUri) ||
                !form.TryGetValue("code_verifier", out string verifier) ||
                !m_Store.TryConsumeAuthorizationCode(
                    code,
                    clientId,
                    redirectUri,
                    verifier,
                    out NexAuthorizationGrant grant))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant", "Authorization code or PKCE verifier is invalid.");
                return;
            }

            if (!TryValidateResidentGrant(grant.Subject, grant.SecurityStamp, out UserAccount account))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant", "Resident session is no longer valid.");
                return;
            }

            string accessToken = m_Tokens.Issue(grant.Subject, grant.Scopes, account.NexVerseStateChanged);
            NexRefreshTokenIssue refresh = null;

            if (grant.Scopes.Contains(NexScopes.OfflineAccess, StringComparer.OrdinalIgnoreCase))
            {
                refresh = m_Store.CreateRefreshToken(
                    clientId,
                    grant.Subject,
                    grant.Scopes,
                    account.NexVerseStateChanged,
                    m_RefreshLifetimeSeconds);
            }

            string idToken = grant.Scopes.Contains(NexScopes.OpenId, StringComparer.OrdinalIgnoreCase)
                ? m_Oidc.IssueIdentityToken(
                    grant.Subject,
                    clientId,
                    grant.Nonce,
                    account.NexVerseStateChanged)
                : string.Empty;

            WriteTokenResponse(response, accessToken, grant.Scopes, refresh?.Token, idToken);
        }

        private void HandleRefreshToken(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Dictionary<string, string> form)
        {
            if (!TryGetClient(request, form, out NexOAuthClient client, out string clientId))
            {
                WriteOAuthError(response, HttpStatusCode.Unauthorized, "invalid_client", "Client authentication failed.");
                return;
            }

            if (!form.TryGetValue("refresh_token", out string refreshToken) ||
                !m_Store.TryRotateRefreshToken(
                    refreshToken,
                    clientId,
                    m_RefreshLifetimeSeconds,
                    out NexRefreshGrant previous,
                    out NexRefreshTokenIssue replacement))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant", "Refresh token is invalid or expired.");
                return;
            }

            if (!TryValidateResidentGrant(previous.Subject, previous.SecurityStamp, out UserAccount account))
            {
                m_Store.RevokeRefreshToken(replacement.Token);
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant", "Resident session is no longer valid.");
                return;
            }

            string accessToken = m_Tokens.Issue(
                previous.Subject,
                previous.Scopes,
                account.NexVerseStateChanged);

            WriteTokenResponse(
                response,
                accessToken,
                previous.Scopes,
                replacement.Token,
                string.Empty);
        }

        private void HandleClientCredentials(
            IOSHttpRequest request,
            IOSHttpResponse response,
            Dictionary<string, string> form)
        {
            if (!TryGetClient(request, form, out NexOAuthClient client, out string clientId) ||
                client.ClientType != NexOAuthClientTypes.Service)
            {
                WriteOAuthError(response, HttpStatusCode.Unauthorized, "invalid_client", "A service client with a valid secret is required.");
                return;
            }

            string[] scopes = form.TryGetValue("scope", out string scopeRaw) && !string.IsNullOrWhiteSpace(scopeRaw)
                ? SplitScopes(scopeRaw)
                : (client.AllowedScopes ?? Array.Empty<string>());

            HashSet<string> allowed = new HashSet<string>(
                client.AllowedScopes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (scopes.Any(x => !allowed.Contains(x)))
            {
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_scope", "A requested scope is not allowed for this service client.");
                return;
            }

            string subject = "service:" + clientId;
            string accessToken = m_Tokens.Issue(subject, scopes, client.SecurityStamp);
            WriteTokenResponse(response, accessToken, scopes, string.Empty, string.Empty);
        }

        private bool TryGetClient(
            IOSHttpRequest request,
            Dictionary<string, string> form,
            out NexOAuthClient client,
            out string clientId)
        {
            client = null;
            clientId = form.TryGetValue("client_id", out string bodyId) ? bodyId : string.Empty;
            string secret = form.TryGetValue("client_secret", out string bodySecret) ? bodySecret : string.Empty;

            if (TryReadBasicClient(request, out string basicId, out string basicSecret))
            {
                clientId = basicId;
                secret = basicSecret;
            }

            client = m_Store.GetClient(clientId);
            if (client == null || !client.Enabled)
                return false;

            if (!client.RequiresSecret)
                return true;

            return m_Store.ValidateClientSecret(clientId, secret);
        }

        private bool TryValidateResidentGrant(
            string subject,
            int securityStamp,
            out UserAccount account)
        {
            account = null;
            if (!UUID.TryParse(subject, out UUID id))
                return false;

            account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            return account != null &&
                   account.LoginAllowed &&
                   account.NexVerseStateChanged == securityStamp;
        }

        private static bool IsIdentityScope(string scope)
        {
            return string.Equals(scope, NexScopes.OpenId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scope, NexScopes.Profile, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scope, NexScopes.OfflineAccess, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] SplitScopes(string value)
        {
            return (value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void WriteTokenResponse(
            IOSHttpResponse response,
            string accessToken,
            IEnumerable<string> scopes,
            string refreshToken,
            string idToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["access_token"] = accessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = m_Tokens.LifetimeSeconds,
                ["scope"] = string.Join(" ", scopes ?? Array.Empty<string>())
            };

            if (!string.IsNullOrEmpty(refreshToken))
                payload["refresh_token"] = refreshToken;
            if (!string.IsNullOrEmpty(idToken))
                payload["id_token"] = idToken;

            WriteJson(response, payload);
        }

        private static object ClientPayload(NexOAuthClient client)
        {
            if (client == null)
                return null;

            return new
            {
                client_id = client.ClientId,
                name = client.Name,
                client_type = client.ClientType,
                redirect_uris = client.RedirectUris,
                allowed_scopes = client.AllowedScopes,
                enabled = client.Enabled,
                security_stamp = client.SecurityStamp,
                created_at = client.CreatedAt,
                updated_at = client.UpdatedAt
            };
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope,
            out NexPrincipal principal,
            out UserAccount account)
        {
            if (m_Authenticator.TryAuthenticate(
                request,
                scope,
                out principal,
                out account,
                out int statusCode,
                out string error))
                return true;

            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteOAuthError(response, (HttpStatusCode)statusCode, error, "Authentication or authorization failed.");
            return false;
        }

        private static bool TryReadBasicClient(
            IOSHttpRequest request,
            out string clientId,
            out string clientSecret)
        {
            clientId = string.Empty;
            clientSecret = string.Empty;

            string authorization = request?.Headers?["Authorization"];
            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                string decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(authorization.Substring("Basic ".Length).Trim()));
                int separator = decoded.IndexOf(':');
                if (separator < 0)
                    return false;

                clientId = WebUtility.UrlDecode(decoded.Substring(0, separator));
                clientSecret = WebUtility.UrlDecode(decoded.Substring(separator + 1));
                return !string.IsNullOrWhiteSpace(clientId);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> ReadForm(IOSHttpRequest request)
        {
            using StreamReader reader = new StreamReader(
                request.InputStream,
                Encoding.UTF8,
                true,
                4096,
                true);

            string body = reader.ReadToEnd();
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                string key = separator < 0 ? pair : pair.Substring(0, separator);
                string value = separator < 0 ? string.Empty : pair.Substring(separator + 1);

                result[WebUtility.UrlDecode(key.Replace('+', ' '))] =
                    WebUtility.UrlDecode(value.Replace('+', ' '));
            }

            return result;
        }

        private static bool TryReadJson(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonDocument document)
        {
            document = null;
            try
            {
                using StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8, true, 4096, true);
                string body = reader.ReadToEnd();
                document = JsonDocument.Parse(body);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException();

                return true;
            }
            catch
            {
                document?.Dispose();
                document = null;
                WriteOAuthError(response, HttpStatusCode.BadRequest, "invalid_json", "A JSON object is required.");
                return false;
            }
        }

        private static bool TryReadOptionalJson(IOSHttpRequest request, out JsonDocument document)
        {
            document = null;
            try
            {
                using StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8, true, 4096, true);
                string body = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body))
                    return false;

                document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch
            {
                document?.Dispose();
                document = null;
                return false;
            }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string[] GetStringArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        private static bool RequireMethod(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string method)
        {
            if (request != null &&
                string.Equals(request.HttpMethod, method, StringComparison.OrdinalIgnoreCase))
                return true;

            WriteOAuthError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", method + " is required.");
            return false;
        }

        private static string Correlation(IOSHttpResponse response)
        {
            return NexApiRequestContext.Ensure(response);
        }

        private static void WriteOAuthError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string description)
        {
            WriteJson(response, new
            {
                error,
                error_description = description,
                correlation_id =
                    NexApiRequestContext.CurrentCorrelationId
            }, status);
        }

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_Json);
        }
    }
}
