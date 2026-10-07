// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Identity;
using NexVerse.Core.Messaging;
using NexVerse.Core.Observability;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    internal sealed class NexApiAuthenticator
    {
        private readonly IUserAccountService m_UserAccounts;
        private readonly INexAuthorizationService m_Authorization;
        private readonly INexAccessTokenService m_NativeTokens;
        private readonly INexOAuthStore m_OAuthStore;
        private readonly INexApiKeyStore m_ApiKeys;
        private readonly int m_AdminMinimumLevel;

        public NexApiAuthenticator(
            IUserAccountService userAccounts,
            INexAuthorizationService authorization,
            INexAccessTokenService nativeTokens,
            INexOAuthStore oauthStore,
            INexApiKeyStore apiKeys,
            int adminMinimumLevel)
        {
            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
            m_Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
            m_NativeTokens = nativeTokens;
            m_OAuthStore = oauthStore;
            m_ApiKeys = apiKeys;
            m_AdminMinimumLevel = adminMinimumLevel;
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

            string apiKey =
                request?.Headers?["X-NexVerse-Api-Key"]?.Trim();

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (m_ApiKeys == null ||
                    !m_ApiKeys.TryValidate(
                        apiKey,
                        out NexApiKeyRecord apiKeyRecord))
                {
                    error = "invalid_api_key";
                    return false;
                }

                principal = new NexPrincipal(
                    "api-key:" + apiKeyRecord.KeyId,
                    apiKeyRecord.Scopes,
                    true);

                if (!string.IsNullOrWhiteSpace(requiredScope) &&
                    !m_Authorization.IsAllowed(
                        principal,
                        requiredScope))
                {
                    statusCode = (int)HttpStatusCode.Forbidden;
                    error = "insufficient_scope";
                    return false;
                }

                statusCode = (int)HttpStatusCode.OK;
                error = string.Empty;
                return true;
            }

            if (!TryReadBearer(request, out string token))
                return false;

            if (m_NativeTokens == null ||
                !m_NativeTokens.TryValidate(
                    token,
                    out NexAccessTokenClaims nativeClaims))
            {
                error = "invalid_access_token";
                return false;
            }

            if (m_OAuthStore != null &&
                m_OAuthStore.IsAccessTokenRevoked(
                    nativeClaims.TokenId))
            {
                error = "access_token_revoked";
                return false;
            }

            if (UUID.TryParse(
                nativeClaims.Subject,
                out UUID principalId))
            {
                account =
                    m_UserAccounts.GetUserAccount(
                        UUID.Zero,
                        principalId);

                if (account == null)
                {
                    error = "account_not_found";
                    return false;
                }

                if (!account.LoginAllowed)
                {
                    statusCode = (int)HttpStatusCode.Forbidden;
                    error = "account_blocked";
                    return false;
                }

                if (nativeClaims.SecurityStamp !=
                    account.NexVerseStateChanged)
                {
                    error = "stale_access_token";
                    return false;
                }

                List<string> scopes =
                    new List<string>(
                        nativeClaims.Scopes);

                string[] currentRoles =
                    NexAuthorizationPolicy.SplitStoredValues(
                        account.NexVerseRoles);
                string[] currentExplicitScopes =
                    NexAuthorizationPolicy.SplitStoredValues(
                        account.NexVerseScopes);
                string[] currentEffectiveScopes =
                    NexAuthorizationPolicy.GetEffectiveScopes(
                        account.UserLevel,
                        m_AdminMinimumLevel,
                        currentRoles,
                        currentExplicitScopes);

                if (!currentEffectiveScopes.Contains(
                        NexScopes.AdminAll,
                        StringComparer.OrdinalIgnoreCase))
                {
                    scopes.RemoveAll(x =>
                        string.Equals(
                            x,
                            NexScopes.AdminAll,
                            StringComparison.OrdinalIgnoreCase));
                }

                principal =
                    new NexPrincipal(
                        principalId.ToString(),
                        scopes,
                        true);
            }
            else if (
                m_OAuthStore != null &&
                m_OAuthStore.ValidateServicePrincipal(
                    nativeClaims.Subject,
                    nativeClaims.SecurityStamp,
                    nativeClaims.Scopes))
            {
                principal =
                    new NexPrincipal(
                        nativeClaims.Subject,
                        nativeClaims.Scopes,
                        true);
            }
            else
            {
                error = "invalid_access_token";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(requiredScope) &&
                !m_Authorization.IsAllowed(
                    principal,
                    requiredScope))
            {
                statusCode = (int)HttpStatusCode.Forbidden;
                error = "insufficient_scope";
                return false;
            }

            statusCode = (int)HttpStatusCode.OK;
            error = string.Empty;
            return true;
        }

        private static bool TryReadBearer(
            IOSHttpRequest request,
            out string token)
        {
            token = string.Empty;

            string authorization =
                request?.Headers?["Authorization"];

            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith(
                    "Bearer ",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            token =
                authorization
                    .Substring("Bearer ".Length)
                    .Trim();

            return !string.IsNullOrWhiteSpace(token);
        }
    }

    internal sealed class OpenSimNexUserService : INexUserService
    {
        private readonly IUserAccountService m_UserAccounts;
        private readonly IAuthenticationService m_Authentication;
        private readonly IInventoryService m_Inventory;
        private readonly IGridUserService m_GridUsers;
        private readonly IGridService m_Grid;

        public OpenSimNexUserService(
            IUserAccountService userAccounts,
            IAuthenticationService authentication,
            IInventoryService inventory,
            IGridUserService gridUsers,
            IGridService grid)
        {
            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
            m_Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            m_Inventory = inventory;
            m_GridUsers = gridUsers;
            m_Grid = grid;
        }

        public NexUserRecord GetById(string principalId)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return null;

            return Convert(m_UserAccounts.GetUserAccount(UUID.Zero, id));
        }

        public NexUserRecord GetByName(string firstName, string lastName)
        {
            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                return null;

            return Convert(m_UserAccounts.GetUserAccount(UUID.Zero, firstName.Trim(), lastName.Trim()));
        }

        public IReadOnlyList<NexUserRecord> Search(string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<NexUserRecord>();

            int safeLimit = Math.Max(1, Math.Min(limit, 10001));
            List<UserAccount> accounts = m_UserAccounts.GetUserAccounts(UUID.Zero, query.Trim());
            if (accounts == null)
                return Array.Empty<NexUserRecord>();

            return accounts
                .Take(safeLimit)
                .Select(Convert)
                .Where(x => x != null)
                .ToList();
        }

        public IReadOnlyList<NexRegionRecord> SearchHomeRegions(string query, int limit)
        {
            if (m_Grid == null)
                return Array.Empty<NexRegionRecord>();

            int safeLimit = Math.Max(1, Math.Min(limit, 10001));
            List<GridRegion> regions;

            if (string.IsNullOrWhiteSpace(query))
            {
                regions = m_Grid.GetDefaultRegions(UUID.Zero);
            }
            else if (UUID.TryParse(query.Trim(), out UUID regionId))
            {
                GridRegion exact = m_Grid.GetRegionByUUID(UUID.Zero, regionId);
                regions = exact == null ? new List<GridRegion>() : new List<GridRegion> { exact };
            }
            else
            {
                regions = m_Grid.GetRegionsByName(UUID.Zero, query.Trim(), safeLimit);
            }

            if (regions == null)
                return Array.Empty<NexRegionRecord>();

            return regions
                .Where(x => x != null && !x.RegionID.IsZero())
                .Take(safeLimit)
                .Select(ConvertRegion)
                .ToList();
        }

        public NexRegionRecord ResolveHomeRegion(string regionId, string regionName)
        {
            if (m_Grid == null)
                return null;

            GridRegion region = null;

            if (!string.IsNullOrWhiteSpace(regionId) && UUID.TryParse(regionId.Trim(), out UUID id))
                region = m_Grid.GetRegionByUUID(UUID.Zero, id);

            if (region == null && !string.IsNullOrWhiteSpace(regionName))
                region = m_Grid.GetRegionByName(UUID.Zero, regionName.Trim());

            return ConvertRegion(region);
        }

        public NexUserProvisionResult Create(
            string firstName,
            string lastName,
            string email,
            string password,
            string homeRegionId,
            float homeX,
            float homeY,
            float homeZ)
        {
            if (GetByName(firstName, lastName) != null)
                return new NexUserProvisionResult(null, false, false, false, false, false, null);

            NexRegionRecord homeRegion = ResolveHomeRegion(homeRegionId, null);
            if (homeRegion == null || m_GridUsers == null)
                return new NexUserProvisionResult(null, false, false, false, false, false, null);

            UserAccount account = new UserAccount(
                UUID.Zero,
                UUID.Random(),
                firstName.Trim(),
                lastName.Trim(),
                email?.Trim() ?? string.Empty);

            account.Active = false;
            account.NexVerseState = NexAccountStates.Provisioning;
            account.NexVerseStateReason = string.Empty;
            account.NexVerseStateChanged = OpenSim.Framework.Util.UnixTimeSinceEpoch();

            if (!m_UserAccounts.StoreUserAccount(account))
                return new NexUserProvisionResult(null, false, false, false, false, false, homeRegion);

            bool authenticationInitialized = m_Authentication.SetPassword(account.PrincipalID, password);
            bool inventoryInitialized = m_Inventory != null && m_Inventory.CreateUserInventory(account.PrincipalID);

            Vector3 position = new Vector3(homeX, homeY, homeZ);
            Vector3 lookAt = new Vector3(0, 1, 0);
            UUID selectedRegionId = new UUID(homeRegion.RegionId);

            bool homeInitialized = m_GridUsers.SetHome(
                account.PrincipalID.ToString(),
                selectedRegionId,
                position,
                lookAt);

            bool startPositionInitialized = m_GridUsers.SetLastPosition(
                account.PrincipalID.ToString(),
                UUID.Zero,
                selectedRegionId,
                position,
                lookAt);

            bool provisioned =
                authenticationInitialized &&
                inventoryInitialized &&
                homeInitialized &&
                startPositionInitialized;

            account.Active = provisioned;
            account.NexVerseState = provisioned
                ? NexAccountStates.Active
                : NexAccountStates.ProvisioningFailed;
            account.NexVerseStateReason = provisioned
                ? string.Empty
                : "One or more mandatory provisioning steps failed.";
            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);

            bool stateFinalized = m_UserAccounts.StoreUserAccount(account);
            m_UserAccounts.InvalidateCache(account.PrincipalID);

            return new NexUserProvisionResult(
                Convert(account),
                authenticationInitialized,
                inventoryInitialized,
                homeInitialized,
                startPositionInitialized,
                stateFinalized,
                homeRegion);
        }

        public NexUserRecord UpdateProfile(
            string principalId,
            string email,
            string userTitle,
            string userCountry)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return null;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return null;

            account.Email = email ?? string.Empty;
            account.UserTitle = userTitle ?? string.Empty;
            account.UserCountry = userCountry ?? string.Empty;

            if (!m_UserAccounts.StoreUserAccount(account))
                return null;

            m_UserAccounts.InvalidateCache(id);
            return Convert(account);
        }

        public NexUserRecord SetDisplayName(
            string principalId,
            string displayName,
            bool bypassCooldown,
            out int retryAfterSeconds,
            out string error)
        {
            retryAfterSeconds = 0;
            error = string.Empty;

            if (!UUID.TryParse(principalId, out UUID id))
            {
                error = "user_not_found";
                return null;
            }

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null || !account.LocalToGrid)
            {
                error = "user_not_found";
                return null;
            }

            if (!DisplayNamePolicy.TryNormalize(displayName, out string normalized, out string validationError))
            {
                error = validationError;
                return null;
            }

            if (string.Equals(
                    normalized,
                    account.EffectiveDisplayName,
                    StringComparison.Ordinal))
            {
                return Convert(account);
            }

            int now = OpenSim.Framework.Util.UnixTimeSinceEpoch();
            if (!bypassCooldown && !DisplayNamePolicy.CanChangeNow(account, now))
            {
                retryAfterSeconds = Math.Max(1, DisplayNamePolicy.NextChangeAt(account) - now);
                error = "display_name_cooldown";
                return null;
            }

            string stored =
                string.Equals(normalized, account.DefaultDisplayName, StringComparison.Ordinal)
                    ? string.Empty
                    : normalized;

            account.DisplayName = stored;
            account.DisplayNameChanged = now;

            if (!m_UserAccounts.StoreUserAccount(account))
            {
                error = "display_name_update_failed";
                return null;
            }

            m_UserAccounts.InvalidateCache(id);
            return Convert(account);
        }

        public bool VerifyPassword(
            string principalId,
            string password)
        {
            if (!UUID.TryParse(
                    principalId,
                    out UUID id) ||
                string.IsNullOrEmpty(password) ||
                password.Length > 256)
            {
                return false;
            }

            UserAccount account =
                m_UserAccounts.GetUserAccount(
                    UUID.Zero,
                    id);

            if (account == null ||
                !account.LoginAllowed)
            {
                return false;
            }

            AuthInfo auth =
                m_Authentication.GetAuthInfo(id);

            if (auth == null ||
                string.IsNullOrWhiteSpace(
                    auth.PasswordHash) ||
                string.IsNullOrWhiteSpace(
                    auth.PasswordSalt))
            {
                return false;
            }

            string candidate =
                OpenSim.Framework.Util.Md5Hash(
                    OpenSim.Framework.Util.Md5Hash(
                        password) +
                    ":" +
                    auth.PasswordSalt);

            byte[] expected =
                Encoding.ASCII.GetBytes(
                    auth.PasswordHash
                        .ToLowerInvariant());

            byte[] actual =
                Encoding.ASCII.GetBytes(
                    candidate
                        .ToLowerInvariant());

            return
                expected.Length == actual.Length &&
                CryptographicOperations.FixedTimeEquals(
                    expected,
                    actual);
        }

        public bool SetUserLevel(string principalId, int userLevel)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return false;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return false;

            account.UserLevel = userLevel;
            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);

            bool stored = m_UserAccounts.StoreUserAccount(account);
            if (stored)
                m_UserAccounts.InvalidateCache(id);

            return stored;
        }

        public NexUserRecord SetAuthorization(
            string principalId,
            IEnumerable<string> roles,
            IEnumerable<string> scopes,
            bool allowPrivilegedGrant,
            out string error)
        {
            error = string.Empty;

            if (!UUID.TryParse(principalId, out UUID id))
            {
                error = "user_not_found";
                return null;
            }

            UserAccount account =
                m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
            {
                error = "user_not_found";
                return null;
            }

            if (!NexAuthorizationPolicy.TryNormalizeAssignment(
                    roles,
                    scopes,
                    allowPrivilegedGrant,
                    out string[] normalizedRoles,
                    out string[] normalizedScopes,
                    out error))
            {
                return null;
            }

            account.NexVerseRoles =
                NexAuthorizationPolicy.JoinStoredValues(
                    normalizedRoles);
            account.NexVerseScopes =
                NexAuthorizationPolicy.JoinStoredValues(
                    normalizedScopes);
            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);

            if (!m_UserAccounts.StoreUserAccount(account))
            {
                error = "authorization_update_failed";
                return null;
            }

            m_UserAccounts.InvalidateCache(id);
            return Convert(account);
        }

        public bool SetPassword(string principalId, string password)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return false;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return false;

            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);

            if (!m_UserAccounts.StoreUserAccount(account))
                return false;

            m_UserAccounts.InvalidateCache(id);
            return m_Authentication.SetPassword(id, password);
        }

        public NexUserRecord SetAccountState(string principalId, string state, string reason)
        {
            if (!UUID.TryParse(principalId, out UUID id) ||
                !NexAccountStates.IsAdministrativeState(state))
                return null;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return null;

            account.NexVerseState = state;
            account.NexVerseStateReason = reason ?? string.Empty;
            account.NexVerseStateChanged = Math.Max(
                OpenSim.Framework.Util.UnixTimeSinceEpoch(),
                account.NexVerseStateChanged + 1);
            account.Active = state != NexAccountStates.Deactivated;

            if (!m_UserAccounts.StoreUserAccount(account))
                return null;

            m_UserAccounts.InvalidateCache(id);
            return Convert(account);
        }

        private static NexRegionRecord ConvertRegion(GridRegion region)
        {
            if (region == null)
                return null;

            int worldX =
                region.RegionLocX;
            int worldY =
                region.RegionLocY;

            return new NexRegionRecord(
                region.RegionID.ToString(),
                region.RegionName,
                region.ServerURI,
                WorldToGridCoordinate(worldX),
                WorldToGridCoordinate(worldY),
                worldX,
                worldY,
                region.RegionSizeX,
                region.RegionSizeY);
        }

        private static int WorldToGridCoordinate(
            int worldCoordinate)
        {
            int cellSize =
                (int)OpenSim.Framework.Constants.RegionSize;
            int quotient =
                worldCoordinate /
                cellSize;
            int remainder =
                worldCoordinate %
                cellSize;

            if (remainder != 0 &&
                worldCoordinate < 0)
            {
                quotient--;
            }

            return quotient;
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
                account.EffectiveDisplayName,
                account.IsDisplayNameDefault,
                account.DisplayNameChanged,
                DisplayNamePolicy.NextChangeAt(account),
                account.LocalToGrid,
                account.Active,
                account.NexVerseState,
                account.NexVerseStateReason,
                account.NexVerseStateChanged,
                NexAuthorizationPolicy.SplitStoredValues(
                    account.NexVerseRoles),
                NexAuthorizationPolicy.SplitStoredValues(
                    account.NexVerseScopes),
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
        private readonly INexAccessTokenService m_Tokens;
        private readonly INexOAuthStore m_OAuthStore;
        private readonly INexAuditStore m_AuditStore;
        private readonly INexIdempotencyStore m_IdempotencyStore;
        private readonly INexApiKeyStore m_ApiKeys;
        private readonly NexGridControlApi m_GridControl;
        private readonly NexRegionMutationApi m_RegionMutations;
        private readonly NexNodeApi m_NodeApi;
        private readonly NexEstateApi m_EstateApi;
        private readonly int m_IdempotencyTtlSeconds;
        private readonly int m_AdminMinimumLevel;
        private readonly INexSecurityStore m_Security;

        public NexUserApiRouter(
            INexUserService users,
            NexApiAuthenticator authenticator,
            INexEventBus eventBus,
            INexAuditSink audit,
            INexAccessTokenService tokens,
            INexOAuthStore oauthStore,
            INexAuditStore auditStore,
            INexIdempotencyStore idempotencyStore,
            int idempotencyTtlSeconds,
            INexApiKeyStore apiKeys,
            IGridService grid,
            IEstateDataService estateData,
            IUserAccountService userAccounts,
            NexNodeRegistry nodeRegistry,
            NexRegionOperationRegistry regionOperationRegistry,
            bool distributedNexBusEnabled,
            int adminMinimumLevel,
            INexSecurityStore securityStore = null)
        {
            m_Users = users ?? throw new ArgumentNullException(nameof(users));
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_Tokens = tokens;
            m_OAuthStore = oauthStore;
            m_AuditStore = auditStore;
            m_IdempotencyStore = idempotencyStore;
            m_IdempotencyTtlSeconds = Math.Max(60, idempotencyTtlSeconds);
            m_ApiKeys = apiKeys;
            m_GridControl = new NexGridControlApi(
                grid,
                authenticator,
                nodeRegistry);
            m_RegionMutations = new NexRegionMutationApi(
                grid,
                estateData,
                nodeRegistry,
                regionOperationRegistry,
                authenticator,
                eventBus,
                m_Audit,
                idempotencyStore,
                m_IdempotencyTtlSeconds,
                distributedNexBusEnabled);
            m_NodeApi = new NexNodeApi(
                nodeRegistry,
                authenticator,
                distributedNexBusEnabled,
                eventBus);
            m_EstateApi = new NexEstateApi(
                estateData,
                grid,
                userAccounts,
                authenticator,
                eventBus,
                m_Audit);
            m_AdminMinimumLevel = adminMinimumLevel;
            m_Security = securityStore;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');

            NexMetricsRegistry.Default.IncrementCounter(
                "nexverse_world_api_privileged_requests_total",
                "Privileged NexVerse World API requests.",
                1,
                new Dictionary<string, string>
                {
                    ["method"] = request?.HttpMethod ?? "unknown"
                });

            using Activity activity = NexTelemetry.ActivitySource.StartActivity(
                "nexverse.world_api.privileged_request",
                ActivityKind.Server);
            activity?.SetTag("http.request.method", request?.HttpMethod ?? string.Empty);
            activity?.SetTag("url.path", path);

            if (string.Equals(path, "/api/v1/auth/session", StringComparison.OrdinalIgnoreCase))
            {
                HandleNativeSessionLogin(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/auth/api-keys", StringComparison.OrdinalIgnoreCase))
            {
                HandleApiKeys(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/auth/authorization-model", StringComparison.OrdinalIgnoreCase))
            {
                HandleAuthorizationModel(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/audit", StringComparison.OrdinalIgnoreCase))
            {
                HandleAuditSearch(request, response, null);
                return;
            }

            if (string.Equals(path, "/api/v1/grid", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/v1/grid/", StringComparison.OrdinalIgnoreCase))
            {
                m_GridControl.Handle(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/nodes", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/v1/nodes/", StringComparison.OrdinalIgnoreCase))
            {
                m_NodeApi.Handle(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/estates", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/v1/estates/", StringComparison.OrdinalIgnoreCase))
            {
                m_EstateApi.Handle(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/regions", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMethod(request, "GET"))
                    HandleRegionSearch(request, response);
                else if (IsMethod(request, "POST"))
                    m_RegionMutations.Handle(request, response);
                else
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "GET or POST is required.");
                return;
            }

            if ((path.StartsWith("/api/v1/regions/", StringComparison.OrdinalIgnoreCase) &&
                 (path.EndsWith("/placement", StringComparison.OrdinalIgnoreCase) ||
                  path.EndsWith("/lifecycle", StringComparison.OrdinalIgnoreCase))) ||
                path.StartsWith("/api/v1/region-operations/", StringComparison.OrdinalIgnoreCase))
            {
                m_RegionMutations.Handle(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/users/me", StringComparison.OrdinalIgnoreCase))
            {
                HandleMe(request, response);
                return;
            }

            if (string.Equals(path, "/api/v1/users", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMethod(request, "GET"))
                    HandleSearch(request, response);
                else if (IsMethod(request, "POST"))
                    HandleCreate(request, response);
                else
                    WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "HTTP method is not allowed for this endpoint.");
                return;
            }

            const string prefix = "/api/v1/users/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string tail = path.Substring(prefix.Length);
                string[] parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 1)
                {
                    if (IsMethod(request, "GET"))
                        HandleGet(request, response, parts[0]);
                    else if (IsMethod(request, "PATCH"))
                        HandleUpdate(request, response, parts[0]);
                    else if (IsMethod(request, "DELETE"))
                        HandleDeactivate(request, response, parts[0]);
                    else
                        WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "HTTP method is not allowed for this endpoint.");
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "audit", StringComparison.OrdinalIgnoreCase))
                {
                    HandleAuditSearch(request, response, parts[0]);
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "state", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSetState(request, response, parts[0]);
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "level", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSetLevel(request, response, parts[0]);
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "authorization", StringComparison.OrdinalIgnoreCase))
                {
                    HandleUserAuthorization(request, response, parts[0]);
                    return;
                }

                if (parts.Length == 2 && string.Equals(parts[1], "password", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSetPassword(request, response, parts[0]);
                    return;
                }
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown NexVerse API endpoint.");
        }

        private void HandleAuthorizationModel(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(
                    request,
                    response,
                    NexScopes.SecurityManage,
                    out NexPrincipal _,
                    out UserAccount _))
                return;

            WriteJson(response, new
            {
                roles = NexRoles.All.Select(role => new
                {
                    id = role,
                    scopes = NexAuthorizationPolicy.GetRoleScopes(role)
                }).ToArray(),
                scopes = NexAuthorizationPolicy.AssignableScopes,
                privileged = new
                {
                    roles = new[] { NexRoles.Administrator },
                    scopes = new[]
                    {
                        NexScopes.AdminAll,
                        NexScopes.SecurityManage
                    },
                    note = "administrator and privileged security grants require admin:*"
                }
            });
        }

        private void HandleUserAuthorization(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string principalId)
        {
            if (!Authenticate(
                    request,
                    response,
                    NexScopes.SecurityManage,
                    out NexPrincipal principal,
                    out UserAccount _))
                return;

            NexUserRecord existing =
                m_Users.GetById(principalId);
            if (existing == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "user_not_found",
                    "User account was not found.");
                return;
            }

            bool callerIsAdmin =
                principal.HasScope(NexScopes.AdminAll);

            bool targetIsAdmin =
                existing.UserLevel >= m_AdminMinimumLevel ||
                existing.Roles.Contains(
                    NexRoles.Administrator,
                    StringComparer.OrdinalIgnoreCase) ||
                existing.ExplicitScopes.Contains(
                    NexScopes.AdminAll,
                    StringComparer.OrdinalIgnoreCase);

            if (IsMethod(request, "GET"))
            {
                WriteJson(
                    response,
                    AuthorizationPayload(existing));
                return;
            }

            if (!RequireMethod(request, response, "PATCH"))
                return;

            if (targetIsAdmin && !callerIsAdmin)
            {
                WriteError(
                    response,
                    HttpStatusCode.Forbidden,
                    "admin_target_requires_admin",
                    "Changing an administrator requires admin:*.");
                return;
            }

            if (!TryReadJson(
                    request,
                    response,
                    out JsonDocument document))
                return;

            using (document)
            {
                JsonElement root =
                    document.RootElement;

                string[] roles =
                    existing.Roles.ToArray();
                string[] scopes =
                    existing.ExplicitScopes.ToArray();

                if (root.TryGetProperty(
                        "roles",
                        out JsonElement rolesElement))
                {
                    if (!TryReadStringArray(
                            rolesElement,
                            out roles))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_roles",
                            "roles must be an array of strings.");
                        return;
                    }
                }

                if (root.TryGetProperty(
                        "scopes",
                        out JsonElement scopesElement))
                {
                    if (!TryReadStringArray(
                            scopesElement,
                            out scopes))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_scopes",
                            "scopes must be an array of strings.");
                        return;
                    }
                }

                if (!root.TryGetProperty("roles", out _) &&
                    !root.TryGetProperty("scopes", out _))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "no_changes",
                        "roles and/or scopes are required.");
                    return;
                }

                NexUserRecord updated =
                    m_Users.SetAuthorization(
                        principalId,
                        roles,
                        scopes,
                        callerIsAdmin,
                        out string error);

                if (updated == null)
                {
                    HttpStatusCode status =
                        error == "user_not_found"
                            ? HttpStatusCode.NotFound
                            : error.Contains(
                                  "requires_admin",
                                  StringComparison.Ordinal)
                                ? HttpStatusCode.Forbidden
                                : HttpStatusCode.BadRequest;

                    WriteError(
                        response,
                        status,
                        error,
                        "The requested roles or scopes could not be assigned.");
                    return;
                }

                m_OAuthStore?.RevokeSubjectRefreshTokens(
                    principalId);

                string correlationId =
                    AddCorrelation(response);

                m_Audit.Record(
                    new NexAuditEvent(
                        principal.Subject,
                        "users.authorization.update",
                        principalId,
                        correlationId,
                        new Dictionary<string, string>
                        {
                            ["roles"] =
                                string.Join(" ", updated.Roles),
                            ["scopes"] =
                                string.Join(" ", updated.ExplicitScopes)
                        }));

                m_EventBus.Publish(
                    new NexEvent(
                        "user.authorization.changed",
                        "nexverse.world-api",
                        new Dictionary<string, string>
                        {
                            ["principal_id"] = principalId
                        },
                        correlationId));

                WriteJson(response, new
                {
                    authorization =
                        AuthorizationPayload(updated),
                    reauthentication_required = true,
                    correlation_id = correlationId
                });
            }
        }

        private static bool TryReadStringArray(
            JsonElement element,
            out string[] values)
        {
            values = Array.Empty<string>();

            if (element.ValueKind != JsonValueKind.Array)
                return false;

            List<string> result =
                new List<string>();

            foreach (JsonElement item in
                     element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;

                string value =
                    (item.GetString() ?? string.Empty)
                        .Trim();

                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value);
            }

            values = result.ToArray();
            return true;
        }

        private void HandleApiKeys(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!Authenticate(
                request,
                response,
                NexScopes.AdminAll,
                out NexPrincipal principal,
                out UserAccount _))
                return;

            if (m_ApiKeys == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "api_key_store_unavailable",
                    "API key storage is not available.");
                return;
            }

            if (IsMethod(request, "GET"))
            {
                WriteJson(response, new
                {
                    keys = m_ApiKeys
                        .List()
                        .Select(ApiKeyPayload)
                        .ToArray()
                });
                return;
            }

            if (!TryReadJson(
                request,
                response,
                out JsonDocument document))
                return;

            using (document)
            {
                JsonElement root = document.RootElement;

                if (IsMethod(request, "POST"))
                {
                    string name =
                        GetOptionalString(root, "name") ??
                        "NexVerse API key";

                    if (!root.TryGetProperty(
                            "scopes",
                            out JsonElement scopesElement) ||
                        scopesElement.ValueKind !=
                            JsonValueKind.Array)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "scopes_required",
                            "scopes must be a JSON array.");
                        return;
                    }

                    string[] scopes = scopesElement
                        .EnumerateArray()
                        .Where(x =>
                            x.ValueKind ==
                            JsonValueKind.String)
                        .Select(x =>
                            x.GetString() ?? string.Empty)
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x))
                        .ToArray();

                    try
                    {
                        NexApiKeyRegistration registration =
                            m_ApiKeys.Create(
                                name,
                                scopes);

                        string correlationId =
                            AddCorrelation(response);

                        m_Audit.Record(
                            new NexAuditEvent(
                                principal.Subject,
                                "auth.api_key.create",
                                registration.Record.KeyId,
                                correlationId,
                                new Dictionary<string, string>
                                {
                                    ["name"] =
                                        registration.Record.Name,
                                    ["scopes"] =
                                        string.Join(
                                            " ",
                                            registration.Record.Scopes)
                                }));

                        m_EventBus.Publish(
                            new NexEvent(
                                "auth.api_key.created",
                                "nexverse.world-api",
                                new Dictionary<string, string>
                                {
                                    ["key_id"] =
                                        registration.Record.KeyId
                                },
                                correlationId));

                        WriteJson(
                            response,
                            new
                            {
                                api_key =
                                    registration.ApiKey,
                                api_key_note =
                                    "shown once; store securely",
                                key =
                                    ApiKeyPayload(
                                        registration.Record),
                                correlation_id =
                                    correlationId
                            },
                            HttpStatusCode.Created);
                    }
                    catch (ArgumentException e)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_api_key_metadata",
                            e.Message);
                    }

                    return;
                }

                if (IsMethod(request, "PATCH"))
                {
                    string keyId =
                        GetOptionalString(root, "key_id");

                    if (string.IsNullOrWhiteSpace(keyId) ||
                        !root.TryGetProperty(
                            "enabled",
                            out JsonElement enabledElement) ||
                        (enabledElement.ValueKind !=
                             JsonValueKind.True &&
                         enabledElement.ValueKind !=
                             JsonValueKind.False))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_request",
                            "key_id and boolean enabled are required.");
                        return;
                    }

                    bool enabled =
                        enabledElement.GetBoolean();

                    if (!m_ApiKeys.SetEnabled(
                        keyId,
                        enabled))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.NotFound,
                            "api_key_not_found",
                            "API key was not found.");
                        return;
                    }

                    NexApiKeyRecord updated =
                        m_ApiKeys.List().FirstOrDefault(x =>
                            string.Equals(
                                x.KeyId,
                                keyId,
                                StringComparison.Ordinal));

                    string correlationId =
                        AddCorrelation(response);

                    m_Audit.Record(
                        new NexAuditEvent(
                            principal.Subject,
                            "auth.api_key.state.update",
                            keyId,
                            correlationId,
                            new Dictionary<string, string>
                            {
                                ["enabled"] =
                                    enabled.ToString()
                            }));

                    m_EventBus.Publish(
                        new NexEvent(
                            "auth.api_key.state.changed",
                            "nexverse.world-api",
                            new Dictionary<string, string>
                            {
                                ["key_id"] = keyId,
                                ["enabled"] =
                                    enabled.ToString()
                            },
                            correlationId));

                    WriteJson(response, new
                    {
                        key = ApiKeyPayload(updated),
                        correlation_id = correlationId
                    });
                    return;
                }
            }

            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                "GET, POST or PATCH is required.");
        }

        private void HandleNativeSessionLogin(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!RequireMethod(
                request,
                response,
                "POST"))
            {
                return;
            }

            if (m_Tokens == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "native_tokens_disabled",
                    "NexVerse native access-token issuance is disabled.");
                return;
            }

            if (!TryReadJson(
                request,
                response,
                out JsonDocument document))
            {
                return;
            }

            using (document)
            {
                JsonElement root =
                    document.RootElement;

                string username =
                    GetOptionalString(
                        root,
                        "username");

                string password =
                    GetOptionalString(
                        root,
                        "password");

                if (string.IsNullOrWhiteSpace(username) ||
                    string.IsNullOrEmpty(password) ||
                    password.Length > 256)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_request",
                        "username and password are required.");
                    return;
                }

                if (!NexResidentNameResolver.TryResolveLoginInput(
                        username,
                        string.Empty,
                        out NexResidentName residentName))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Unauthorized,
                        "invalid_credentials",
                        "Resident credentials are invalid.");
                    return;
                }

                NexUserRecord user =
                    m_Users.GetByName(
                        residentName.FirstName,
                        residentName.LastName);

                if (user == null ||
                    !m_Users.VerifyPassword(
                        user.PrincipalId,
                        password))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Unauthorized,
                        "invalid_credentials",
                        "Resident credentials are invalid.");
                    return;
                }

                string[] scopes =
                    NexAuthorizationPolicy.GetEffectiveScopes(
                        user.UserLevel,
                        m_AdminMinimumLevel,
                        user.Roles,
                        user.ExplicitScopes);

                if (m_Security != null && m_Security.IsTotpEnabled(user.PrincipalId))
                {
                    string totp = GetOptionalString(root, "totp");
                    bool mfaOk = m_Security.VerifyTotp(user.PrincipalId, totp);
                    m_Security.Record(user.PrincipalId, mfaOk ? "login.mfa.success" : "login.mfa.failed", mfaOk);
                    if (!mfaOk)
                    {
                        WriteError(response, HttpStatusCode.Unauthorized, "mfa_required", "A valid TOTP code is required for this account.");
                        return;
                    }
                }

                string accessToken =
                    m_Tokens.Issue(
                        user.PrincipalId,
                        scopes,
                        user.AccountStateChanged);

                NexSecuritySession securitySession = m_Security?.CreateSession(user.PrincipalId, "native-world-api");

                string correlationId =
                    AddCorrelation(response);

                response.AddHeader(
                    "Cache-Control",
                    "no-store");
                response.AddHeader(
                    "Pragma",
                    "no-cache");

                m_Audit.Record(
                    new NexAuditEvent(
                        user.PrincipalId,
                        "auth.session.login",
                        user.PrincipalId,
                        correlationId,
                        new Dictionary<string, string>
                        {
                            ["scope"] =
                                string.Join(
                                    " ",
                                    scopes)
                        }));

                m_EventBus.Publish(
                    new NexEvent(
                        "auth.session.created",
                        "nexverse.world-api",
                        new Dictionary<string, string>
                        {
                            ["principal_id"] =
                                user.PrincipalId
                        },
                        correlationId));

                WriteJson(
                    response,
                    new
                    {
                        access_token =
                            accessToken,
                        token_type =
                            "Bearer",
                        expires_in =
                            m_Tokens.LifetimeSeconds,
                        scope =
                            string.Join(
                                " ",
                                scopes),
                        principal_id =
                            user.PrincipalId,
                        session_id = securitySession?.Id,
                        correlation_id =
                            correlationId
                    });
            }
        }

        private void HandleAuditSearch(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string forcedResource)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(
                request,
                response,
                NexScopes.AdminAll,
                out NexPrincipal _,
                out UserAccount _))
                return;

            if (m_AuditStore == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "audit_store_unavailable",
                    "Persistent audit storage is not available.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(forcedResource) &&
                m_Users.GetById(forcedResource) == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "user_not_found",
                    "User account was not found.");
                return;
            }

            if (!TryGetPagination(
                request,
                response,
                out int limit,
                out int offset))
                return;

            string resource = string.IsNullOrWhiteSpace(forcedResource)
                ? request.QueryString?["resource"]
                : forcedResource;

            string actor = request.QueryString?["actor"];
            string action = request.QueryString?["action"];

            NexAuditQueryResult result = m_AuditStore.Query(
                resource,
                actor,
                action,
                limit,
                offset);

            WriteJson(response, new
            {
                count = result.Events.Count,
                events = result.Events.Select(AuditPayload).ToArray(),
                pagination = PaginationPayload(
                    limit,
                    offset,
                    result.Events.Count,
                    result.HasMore)
            });
        }

        private void HandleRegionSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, NexScopes.RegionsRead, out NexPrincipal _, out UserAccount _))
                return;

            string query = request.QueryString?["q"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(query) && query.Trim().Length < 2)
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_query", "Region search query must be empty or contain at least two characters.");
                return;
            }

            if (!TryGetPagination(request, response, out int limit, out int offset))
                return;

            int fetchLimit = Math.Min(10001, offset + limit + 1);
            IReadOnlyList<NexRegionRecord> matches =
                m_Users.SearchHomeRegions(query, fetchLimit);

            if (!TryGetSort(
                request,
                response,
                new[] { "name", "size_x", "size_y" },
                "name",
                out string sort,
                out bool descending))
                return;

            IEnumerable<NexRegionRecord> orderedRegions = sort switch
            {
                "size_x" => descending
                    ? matches.OrderByDescending(x => x.SizeX).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    : matches.OrderBy(x => x.SizeX).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                "size_y" => descending
                    ? matches.OrderByDescending(x => x.SizeY).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    : matches.OrderBy(x => x.SizeY).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                _ => descending
                    ? matches.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    : matches.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            };

            NexRegionRecord[] regionResults = orderedRegions
                .Skip(offset)
                .Take(limit + 1)
                .ToArray();

            bool hasMore = regionResults.Length > limit;
            NexRegionRecord[] regions = regionResults
                .Take(limit)
                .ToArray();

            WriteJson(response, new
            {
                count = regions.Length,
                regions = regions.Select(RegionPayload).ToArray(),
                pagination = PaginationPayload(
                    limit,
                    offset,
                    regions.Length,
                    hasMore)
            });
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

            if (!TryGetPagination(request, response, out int limit, out int offset))
                return;

            int fetchLimit = Math.Min(10001, offset + limit + 1);
            IReadOnlyList<NexUserRecord> matches =
                m_Users.Search(query, fetchLimit);

            string state = request.QueryString?["state"];
            if (!string.IsNullOrWhiteSpace(state))
            {
                state = state.Trim().ToLowerInvariant();
                if (!IsKnownAccountState(state))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_filter",
                        "state must be active, locked, banned, deactivated, provisioning or provisioning_failed.");
                    return;
                }
            }

            if (!TryGetSort(
                request,
                response,
                new[] { "name", "created", "user_level", "state" },
                "name",
                out string sort,
                out bool descending))
                return;

            IEnumerable<NexUserRecord> filtered = matches;
            if (!string.IsNullOrWhiteSpace(state))
            {
                filtered = filtered.Where(x =>
                    string.Equals(
                        x.AccountState,
                        state,
                        StringComparison.OrdinalIgnoreCase));
            }

            IOrderedEnumerable<NexUserRecord> orderedUsers = sort switch
            {
                "created" => descending
                    ? filtered.OrderByDescending(x => x.Created)
                    : filtered.OrderBy(x => x.Created),
                "user_level" => descending
                    ? filtered.OrderByDescending(x => x.UserLevel).ThenBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(x => x.UserLevel).ThenBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase),
                "state" => descending
                    ? filtered.OrderByDescending(x => x.AccountState, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(x => x.AccountState, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase),
                _ => descending
                    ? filtered.OrderByDescending(x => x.FirstName, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.LastName, StringComparer.OrdinalIgnoreCase)
                    : filtered.OrderBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.LastName, StringComparer.OrdinalIgnoreCase)
            };

            NexUserRecord[] userResults = orderedUsers
                .Skip(offset)
                .Take(limit + 1)
                .ToArray();

            bool hasMore = userResults.Length > limit;
            NexUserRecord[] users = userResults
                .Take(limit)
                .ToArray();

            WriteJson(response, new
            {
                count = users.Length,
                users = users.Select(UserPayload).ToArray(),
                pagination = PaginationPayload(
                    limit,
                    offset,
                    users.Length,
                    hasMore)
            });
        }

        private void HandleCreate(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "POST"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out UserAccount _))
                return;

            if (!TryReadJson(request, response, out JsonDocument document))
                return;

            using (document)
            {
                JsonElement root = document.RootElement;
                if (!TryGetRequiredString(root, "first_name", out string firstName) ||
                    !TryGetRequiredString(root, "last_name", out string lastName) ||
                    !TryGetRequiredString(root, "password", out string password))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "missing_fields", "first_name, last_name and password are required.");
                    return;
                }

                if (!root.TryGetProperty("home_region", out JsonElement homeRegionElement) ||
                    homeRegionElement.ValueKind != JsonValueKind.Object)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "home_region_required", "home_region must select a valid start/home region.");
                    return;
                }

                string homeRegionId = GetOptionalString(homeRegionElement, "id") ?? string.Empty;
                string homeRegionName = GetOptionalString(homeRegionElement, "name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(homeRegionId) && string.IsNullOrWhiteSpace(homeRegionName))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "home_region_required", "home_region.id or home_region.name is required.");
                    return;
                }

                NexRegionRecord selectedHome = m_Users.ResolveHomeRegion(homeRegionId, homeRegionName);
                if (selectedHome == null)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "home_region_not_found", "The selected home/start region does not exist.");
                    return;
                }

                float homeX = 128f;
                float homeY = 128f;
                float homeZ = 25f;

                if (homeRegionElement.TryGetProperty("position", out JsonElement positionElement))
                {
                    if (positionElement.ValueKind != JsonValueKind.Object ||
                        !TryGetOptionalSingle(positionElement, "x", ref homeX) ||
                        !TryGetOptionalSingle(positionElement, "y", ref homeY) ||
                        !TryGetOptionalSingle(positionElement, "z", ref homeZ))
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_home_position", "home_region.position x, y and z must be numeric.");
                        return;
                    }
                }

                if (homeX < 0 || homeX >= selectedHome.SizeX ||
                    homeY < 0 || homeY >= selectedHome.SizeY ||
                    homeZ < 0 || homeZ > 4096)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_home_position", "The selected home position is outside the valid region bounds.");
                    return;
                }

                string email = GetOptionalString(root, "email") ?? string.Empty;

                firstName = firstName.Trim();
                lastName = lastName.Trim();
                email = email.Trim();

                if (!IsValidNamePart(firstName) || !IsValidNamePart(lastName))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_username", "Name parts must contain 1-64 characters and may not contain whitespace, @, ., : or ;.");
                    return;
                }

                if (!IsValidPassword(password))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_password", "Password must contain between 8 and 256 characters.");
                    return;
                }

                if (!IsValidEmail(email))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_email", "Email must be empty or a valid address of at most 64 characters.");
                    return;
                }

                string idempotencyKey =
                    request?.Headers?["Idempotency-Key"]?.Trim();
                string idempotencyScope = string.Empty;
                string idempotencyHash = string.Empty;
                bool idempotencyReserved = false;
                bool idempotencyCompleted = false;

                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    if (idempotencyKey.Length > 128)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_idempotency_key",
                            "Idempotency-Key may contain at most 128 characters.");
                        return;
                    }

                    if (m_IdempotencyStore == null)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.ServiceUnavailable,
                            "idempotency_unavailable",
                            "Persistent idempotency storage is not available.");
                        return;
                    }

                    string canonicalPayload =
                        JsonSerializer.Serialize(root);

                    idempotencyHash = Convert
                        .ToHexString(
                            SHA256.HashData(
                                Encoding.UTF8.GetBytes(
                                    canonicalPayload)))
                        .ToLowerInvariant();

                    idempotencyScope =
                        principal.Subject +
                        "|POST|/api/v1/users";

                    NexIdempotencyBeginResult begin =
                        m_IdempotencyStore.TryBegin(
                            idempotencyScope,
                            idempotencyKey,
                            idempotencyHash,
                            m_IdempotencyTtlSeconds);

                    if (begin.State ==
                        NexIdempotencyBeginState.Replay)
                    {
                        response.AddHeader(
                            "Idempotency-Replayed",
                            "true");
                        response.KeepAlive = false;
                        response.StatusCode =
                            begin.Response.StatusCode;
                        response.ContentType =
                            begin.Response.ContentType;
                        response.RawBuffer =
                            begin.Response.Body;
                        return;
                    }

                    if (begin.State ==
                        NexIdempotencyBeginState.Conflict)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.Conflict,
                            "idempotency_key_conflict",
                            "Idempotency-Key was already used with a different request payload.");
                        return;
                    }

                    if (begin.State ==
                        NexIdempotencyBeginState.InProgress)
                    {
                        response.AddHeader("Retry-After", "1");
                        WriteError(
                            response,
                            HttpStatusCode.Conflict,
                            "idempotency_in_progress",
                            "A request with this Idempotency-Key is already in progress.");
                        return;
                    }

                    idempotencyReserved = true;
                    response.AddHeader(
                        "Idempotency-Replayed",
                        "false");
                }

                try
                {
                    if (m_Users.GetByName(firstName, lastName) != null)
                    {
                        WriteError(response, HttpStatusCode.Conflict, "user_exists", "A user with this name already exists.");
                        CompleteIdempotency(
                            response,
                            idempotencyReserved,
                            idempotencyScope,
                            idempotencyKey,
                            idempotencyHash);
                        idempotencyCompleted = true;
                        return;
                    }

                    NexUserProvisionResult result = m_Users.Create(
                    firstName,
                    lastName,
                    email,
                    password,
                    selectedHome.RegionId,
                    homeX,
                    homeY,
                    homeZ);
                    if (result?.User == null)
                    {
                        WriteError(response, HttpStatusCode.InternalServerError, "user_create_failed", "The user account could not be created.");
                        CompleteIdempotency(
                            response,
                            idempotencyReserved,
                            idempotencyScope,
                            idempotencyKey,
                            idempotencyHash);
                        idempotencyCompleted = true;
                        return;
                    }

                    string correlationId = AddCorrelation(response);
                m_Audit.Record(new NexAuditEvent(
                    principal.Subject,
                    "users.create",
                    result.User.PrincipalId,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["first_name"] = result.User.FirstName,
                        ["last_name"] = result.User.LastName,
                        ["home_region_id"] = selectedHome.RegionId,
                        ["home_region_name"] = selectedHome.Name
                    }));

                m_EventBus.Publish(new NexEvent(
                    "user.created",
                    "nexverse.world-api",
                    new Dictionary<string, string>
                    {
                        ["principal_id"] = result.User.PrincipalId,
                        ["home_region_id"] = selectedHome.RegionId
                    },
                    correlationId));

                    WriteJson(response, new
                    {
                        user = UserPayload(result.User),
                        provisioning = new
                        {
                            ready = result.Ready,
                            authentication_initialized = result.AuthenticationInitialized,
                            inventory_initialized = result.InventoryInitialized,
                            home_initialized = result.HomeInitialized,
                            start_position_initialized = result.StartPositionInitialized,
                            state_finalized = result.StateFinalized,
                            account_state = result.User.AccountState,
                            home_region = RegionPayload(result.HomeRegion)
                        },
                        correlation_id = correlationId
                    }, HttpStatusCode.Created);

                    CompleteIdempotency(
                        response,
                        idempotencyReserved,
                        idempotencyScope,
                        idempotencyKey,
                        idempotencyHash);
                    idempotencyCompleted = true;
                }
                finally
                {
                    if (idempotencyReserved &&
                        !idempotencyCompleted)
                    {
                        m_IdempotencyStore.Abort(
                            idempotencyScope,
                            idempotencyKey,
                            idempotencyHash);
                    }
                }
            }
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

        private void HandleUpdate(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "PATCH"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount _))
                return;

            bool self = string.Equals(principal.Subject, principalId, StringComparison.OrdinalIgnoreCase);
            bool admin = principal.HasScope(NexScopes.AdminAll);
            if (!self && !admin)
            {
                WriteError(response, HttpStatusCode.Forbidden, "insufficient_scope", "Updating another user requires administrative scope.");
                return;
            }

            NexUserRecord existing = m_Users.GetById(principalId);
            if (existing == null)
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found.");
                return;
            }

            if (!TryReadJson(request, response, out JsonDocument document))
                return;

            using (document)
            {
                JsonElement root = document.RootElement;
                string email = existing.Email;
                string userTitle = existing.UserTitle;
                string userCountry = existing.UserCountry;
                string displayName = existing.DisplayName;
                bool displayNameRequested = false;
                List<string> changed = new List<string>();

                if (root.TryGetProperty("email", out JsonElement emailElement))
                {
                    if (emailElement.ValueKind != JsonValueKind.String)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_email", "email must be a string.");
                        return;
                    }

                    email = (emailElement.GetString() ?? string.Empty).Trim();
                    if (!IsValidEmail(email))
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_email", "Email must be empty or a valid address of at most 254 characters.");
                        return;
                    }
                    changed.Add("email");
                }

                if (root.TryGetProperty("user_country", out JsonElement countryElement))
                {
                    if (countryElement.ValueKind != JsonValueKind.String)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_user_country", "user_country must be a string.");
                        return;
                    }

                    userCountry = (countryElement.GetString() ?? string.Empty).Trim();
                    if (userCountry.Length > 64)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_user_country", "user_country may contain at most 64 characters.");
                        return;
                    }
                    changed.Add("user_country");
                }

                if (root.TryGetProperty("display_name", out JsonElement displayNameElement))
                {
                    if (displayNameElement.ValueKind != JsonValueKind.String)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_display_name", "display_name must be a string.");
                        return;
                    }

                    displayName = displayNameElement.GetString() ?? string.Empty;
                    if (!DisplayNamePolicy.TryNormalize(displayName, out displayName, out string displayNameError))
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_display_name", displayNameError);
                        return;
                    }

                    displayNameRequested = true;
                    changed.Add("display_name");
                }

                if (root.TryGetProperty("user_title", out JsonElement titleElement))
                {
                    if (!admin)
                    {
                        WriteError(response, HttpStatusCode.Forbidden, "insufficient_scope", "Changing user_title requires administrative scope.");
                        return;
                    }

                    if (titleElement.ValueKind != JsonValueKind.String)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_user_title", "user_title must be a string.");
                        return;
                    }

                    userTitle = (titleElement.GetString() ?? string.Empty).Trim();
                    if (userTitle.Length > 64)
                    {
                        WriteError(response, HttpStatusCode.BadRequest, "invalid_user_title", "user_title may contain at most 64 characters.");
                        return;
                    }
                    changed.Add("user_title");
                }

                if (changed.Count == 0)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "no_changes", "No supported account fields were supplied.");
                    return;
                }

                if (displayNameRequested &&
                    !admin &&
                    !string.Equals(
                        displayName,
                        existing.DisplayName,
                        StringComparison.Ordinal) &&
                    existing.DisplayNameNextUpdate >
                        OpenSim.Framework.Util.UnixTimeSinceEpoch())
                {
                    int retryAfterSeconds =
                        Math.Max(
                            1,
                            existing.DisplayNameNextUpdate -
                            OpenSim.Framework.Util.UnixTimeSinceEpoch());

                    response.AddHeader(
                        "Retry-After",
                        retryAfterSeconds.ToString());

                    WriteError(
                        response,
                        HttpStatusCode.TooManyRequests,
                        "display_name_cooldown",
                        "Display name can be changed again after the current seven-day cooldown.");
                    return;
                }

                NexUserRecord updated = m_Users.UpdateProfile(principalId, email, userTitle, userCountry);
                if (updated == null)
                {
                    WriteError(response, HttpStatusCode.InternalServerError, "user_update_failed", "The user account could not be updated.");
                    return;
                }

                if (displayNameRequested)
                {
                    updated = m_Users.SetDisplayName(
                        principalId,
                        displayName,
                        admin,
                        out int retryAfterSeconds,
                        out string displayNameError);

                    if (updated == null)
                    {
                        if (string.Equals(displayNameError, "display_name_cooldown", StringComparison.Ordinal))
                        {
                            response.AddHeader("Retry-After", retryAfterSeconds.ToString());
                            WriteError(
                                response,
                                HttpStatusCode.TooManyRequests,
                                "display_name_cooldown",
                                "Display name can be changed again after the current seven-day cooldown.");
                            return;
                        }

                        WriteError(
                            response,
                            displayNameError == "user_not_found"
                                ? HttpStatusCode.NotFound
                                : HttpStatusCode.InternalServerError,
                            displayNameError,
                            displayNameError == "user_not_found"
                                ? "User account was not found."
                                : "Display name could not be updated.");
                        return;
                    }
                }

                string correlationId = AddCorrelation(response);
                m_Audit.Record(new NexAuditEvent(
                    principal.Subject,
                    "users.update",
                    principalId,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["fields"] = string.Join(",", changed)
                    }));

                m_EventBus.Publish(new NexEvent(
                    "user.updated",
                    "nexverse.world-api",
                    new Dictionary<string, string>
                    {
                        ["principal_id"] = principalId,
                        ["fields"] = string.Join(",", changed)
                    },
                    correlationId));

                WriteJson(response, new
                {
                    user = UserPayload(updated),
                    correlation_id = correlationId
                });
            }
        }

        private void HandleSetState(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "PATCH"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out UserAccount _))
                return;

            if (!TryReadJson(request, response, out JsonDocument document))
                return;

            using (document)
            {
                if (!TryGetRequiredString(document.RootElement, "state", out string requestedState))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "state_required", "state is required.");
                    return;
                }

                string state = requestedState.Trim().ToLowerInvariant();
                if (!NexAccountStates.IsAdministrativeState(state))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_account_state", "state must be active, locked, banned or deactivated.");
                    return;
                }

                string reason = (GetOptionalString(document.RootElement, "reason") ?? string.Empty).Trim();
                if (reason.Length > 255)
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_state_reason", "reason may contain at most 255 characters.");
                    return;
                }

                if (state != NexAccountStates.Active && string.IsNullOrWhiteSpace(reason))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "state_reason_required", "A reason is required when blocking or deactivating an account.");
                    return;
                }

                NexUserRecord updated = m_Users.SetAccountState(principalId, state, reason);
                if (updated == null)
                {
                    WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found or could not be updated.");
                    return;
                }

                m_OAuthStore?.RevokeSubjectRefreshTokens(principalId);

                string correlationId = AddCorrelation(response);
                m_Audit.Record(new NexAuditEvent(
                    principal.Subject,
                    "users.state.update",
                    principalId,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["state"] = state,
                        ["reason"] = reason
                    }));

                m_EventBus.Publish(new NexEvent(
                    "user.state.changed",
                    "nexverse.world-api",
                    new Dictionary<string, string>
                    {
                        ["principal_id"] = principalId,
                        ["state"] = state
                    },
                    correlationId));

                WriteJson(response, new
                {
                    user = UserPayload(updated),
                    correlation_id = correlationId
                });
            }
        }

        private void HandleDeactivate(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "DELETE"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal principal, out UserAccount _))
                return;

            NexUserRecord updated = m_Users.SetAccountState(
                principalId,
                NexAccountStates.Deactivated,
                "soft_delete");

            if (updated == null)
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found or could not be deactivated.");
                return;
            }

            m_OAuthStore?.RevokeSubjectRefreshTokens(principalId);

            string correlationId = AddCorrelation(response);
            m_Audit.Record(new NexAuditEvent(
                principal.Subject,
                "users.deactivate",
                principalId,
                correlationId,
                new Dictionary<string, string> { ["mode"] = "soft_delete" }));

            m_EventBus.Publish(new NexEvent(
                "user.deactivated",
                "nexverse.world-api",
                new Dictionary<string, string> { ["principal_id"] = principalId },
                correlationId));

            WriteJson(response, new
            {
                user = UserPayload(updated),
                correlation_id = correlationId
            });
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

        private void HandleSetPassword(IOSHttpRequest request, IOSHttpResponse response, string principalId)
        {
            if (!RequireMethod(request, response, "POST"))
                return;

            if (!Authenticate(request, response, null, out NexPrincipal principal, out UserAccount _))
                return;

            bool self = string.Equals(principal.Subject, principalId, StringComparison.OrdinalIgnoreCase);
            if (!self && !principal.HasScope(NexScopes.AdminAll))
            {
                WriteError(response, HttpStatusCode.Forbidden, "insufficient_scope", "Resetting another user's password requires administrative scope.");
                return;
            }

            if (m_Users.GetById(principalId) == null)
            {
                WriteError(response, HttpStatusCode.NotFound, "user_not_found", "User account was not found.");
                return;
            }

            if (!TryReadJson(request, response, out JsonDocument document))
                return;

            using (document)
            {
                if (!TryGetRequiredString(document.RootElement, "new_password", out string password) ||
                    !IsValidPassword(password))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_password", "new_password must contain between 8 and 256 characters.");
                    return;
                }

                if (!m_Users.SetPassword(principalId, password))
                {
                    WriteError(response, HttpStatusCode.InternalServerError, "password_update_failed", "The password could not be updated.");
                    return;
                }

                m_OAuthStore?.RevokeSubjectRefreshTokens(principalId);

                string correlationId = AddCorrelation(response);
                m_Audit.Record(new NexAuditEvent(
                    principal.Subject,
                    "users.password.update",
                    principalId,
                    correlationId));

                m_EventBus.Publish(new NexEvent(
                    "user.password.changed",
                    "nexverse.world-api",
                    new Dictionary<string, string>
                    {
                        ["principal_id"] = principalId
                    },
                    correlationId));

                WriteJson(response, new
                {
                    principal_id = principalId,
                    updated = true,
                    correlation_id = correlationId
                });
            }
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

        private static bool IsMethod(IOSHttpRequest request, string method)
        {
            return request != null && string.Equals(request.HttpMethod, method, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RequireMethod(IOSHttpRequest request, IOSHttpResponse response, string method)
        {
            if (IsMethod(request, method))
                return true;

            WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "HTTP method is not allowed for this endpoint.");
            return false;
        }

        private static bool TryReadJson(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonDocument document)
        {
            document = null;

            try
            {
                using StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8, true, 1024, true);
                string body = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body))
                {
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "Request body must contain a JSON object.");
                    return false;
                }

                document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    document = null;
                    WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "Request body must contain a JSON object.");
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                document?.Dispose();
                document = null;
                WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "Request body must contain valid JSON.");
                return false;
            }
        }

        private static bool TryGetRequiredString(JsonElement root, string propertyName, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(propertyName, out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
                return false;

            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string GetOptionalString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element))
                return null;

            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        private void CompleteIdempotency(
            IOSHttpResponse response,
            bool reserved,
            string scope,
            string key,
            string requestHash)
        {
            if (!reserved ||
                m_IdempotencyStore == null)
                return;

            m_IdempotencyStore.Complete(
                scope,
                key,
                requestHash,
                response.StatusCode,
                response.ContentType,
                response.RawBuffer,
                m_IdempotencyTtlSeconds);
        }

        private static bool TryGetSort(
            IOSHttpRequest request,
            IOSHttpResponse response,
            IReadOnlyCollection<string> allowed,
            string defaultSort,
            out string sort,
            out bool descending)
        {
            sort = request?.QueryString?["sort"];
            if (string.IsNullOrWhiteSpace(sort))
                sort = defaultSort;

            sort = sort.Trim().ToLowerInvariant();
            if (!allowed.Contains(sort, StringComparer.OrdinalIgnoreCase))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_sort",
                    "Unsupported sort field.");
                descending = false;
                return false;
            }

            string order = request?.QueryString?["order"];
            if (string.IsNullOrWhiteSpace(order))
                order = "asc";

            order = order.Trim().ToLowerInvariant();
            if (order != "asc" && order != "desc")
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_sort_order",
                    "order must be asc or desc.");
                descending = false;
                return false;
            }

            descending = order == "desc";
            return true;
        }

        private static bool IsKnownAccountState(string state)
        {
            return state == NexAccountStates.Active ||
                   state == NexAccountStates.Locked ||
                   state == NexAccountStates.Banned ||
                   state == NexAccountStates.Deactivated ||
                   state == NexAccountStates.Provisioning ||
                   state == NexAccountStates.ProvisioningFailed;
        }

        private static bool TryGetPagination(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out int limit,
            out int offset)
        {
            limit = 50;
            offset = 0;

            string limitRaw = request?.QueryString?["limit"];
            if (!string.IsNullOrWhiteSpace(limitRaw) &&
                (!Int32.TryParse(limitRaw, out limit) ||
                 limit < 1 ||
                 limit > 100))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_pagination",
                    "limit must be an integer between 1 and 100.");
                return false;
            }

            string offsetRaw = request?.QueryString?["offset"];
            if (!string.IsNullOrWhiteSpace(offsetRaw) &&
                (!Int32.TryParse(offsetRaw, out offset) ||
                 offset < 0 ||
                 offset > 10000))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_pagination",
                    "offset must be an integer between 0 and 10000.");
                return false;
            }

            return true;
        }

        private static object PaginationPayload(
            int limit,
            int offset,
            int returned,
            bool hasMore)
        {
            return new
            {
                limit,
                offset,
                returned,
                has_more = hasMore,
                next_offset = hasMore
                    ? offset + returned
                    : (int?)null
            };
        }

        private static bool TryGetOptionalSingle(JsonElement root, string propertyName, ref float value)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement element))
                return true;

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out float parsed) || float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;

            value = parsed;
            return true;
        }

        private static bool IsValidNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
                return false;

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == '@' || c == '.' || c == ':' || c == ';')
                    return false;
            }

            return true;
        }

        private static bool IsValidPassword(string value)
        {
            return value != null && value.Length >= 8 && value.Length <= 256;
        }

        private static bool IsValidEmail(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            return value.Length <= 64 &&
                   value.IndexOf('@') > 0 &&
                   value.IndexOf('\r') < 0 &&
                   value.IndexOf('\n') < 0;
        }

        private static object ApiKeyPayload(
            NexApiKeyRecord record)
        {
            if (record == null)
                return null;

            return new
            {
                key_id = record.KeyId,
                name = record.Name,
                scopes = record.Scopes,
                enabled = record.Enabled,
                created_at = record.CreatedAt,
                updated_at = record.UpdatedAt
            };
        }

        private static Dictionary<string, object> AuditPayload(
            NexAuditEvent auditEvent)
        {
            return new Dictionary<string, object>
            {
                ["event_id"] = auditEvent.EventId,
                ["timestamp"] = auditEvent.Timestamp,
                ["actor"] = auditEvent.Actor,
                ["action"] = auditEvent.Action,
                ["resource"] = auditEvent.Resource,
                ["correlation_id"] = auditEvent.CorrelationId,
                ["details"] = auditEvent.Details
            };
        }

        private static Dictionary<string, object> RegionPayload(NexRegionRecord region)
        {
            if (region == null)
                return null;

            return new Dictionary<string, object>
            {
                ["region_id"] = region.RegionId,
                ["name"] = region.Name,
                ["server_uri"] = region.ServerUri,
                ["grid_x"] = region.GridX,
                ["grid_y"] = region.GridY,
                ["world_x"] = region.WorldX,
                ["world_y"] = region.WorldY,
                ["size_x"] = region.SizeX,
                ["size_y"] = region.SizeY
            };
        }

        private static string UserName(NexUserRecord user)
        {
            if (user == null)
                return string.Empty;

            if (string.Equals(
                    user.LastName,
                    "Resident",
                    StringComparison.OrdinalIgnoreCase))
            {
                return (user.FirstName ?? string.Empty)
                    .ToLowerInvariant();
            }

            return ((user.FirstName ?? string.Empty) +
                    "." +
                    (user.LastName ?? string.Empty))
                .Trim('.')
                .ToLowerInvariant();
        }

        private static Dictionary<string, object> UserPayload(NexUserRecord user)
        {
            return new Dictionary<string, object>
            {
                ["principal_id"] = user.PrincipalId,
                ["first_name"] = user.FirstName,
                ["last_name"] = user.LastName,
                ["username"] = UserName(user),
                ["email"] = user.Email,
                ["user_level"] = user.UserLevel,
                ["user_flags"] = user.UserFlags,
                ["user_title"] = user.UserTitle,
                ["user_country"] = user.UserCountry,
                ["display_name"] = user.DisplayName,
                ["is_display_name_default"] = user.IsDisplayNameDefault,
                ["display_name_changed"] = user.DisplayNameChanged,
                ["display_name_next_update"] = user.DisplayNameNextUpdate,
                ["local_to_grid"] = user.LocalToGrid,
                ["active"] = user.Active,
                ["account_state"] = user.AccountState,
                ["account_state_reason"] = user.AccountStateReason,
                ["account_state_changed"] = user.AccountStateChanged,
                ["roles"] = user.Roles,
                ["explicit_scopes"] = user.ExplicitScopes,
                ["created"] = user.Created
            };
        }

        private object AuthorizationPayload(
            NexUserRecord user)
        {
            return new
            {
                principal_id = user.PrincipalId,
                roles = user.Roles,
                explicit_scopes = user.ExplicitScopes,
                effective_scopes =
                    NexAuthorizationPolicy.GetEffectiveScopes(
                        user.UserLevel,
                        m_AdminMinimumLevel,
                        user.Roles,
                        user.ExplicitScopes),
                legacy_user_level = user.UserLevel,
                legacy_admin =
                    user.UserLevel >= m_AdminMinimumLevel
            };
        }

        private static void WriteUser(IOSHttpResponse response, NexUserRecord user)
        {
            WriteJson(response, UserPayload(user));
        }

        private static string AddCorrelation(IOSHttpResponse response)
        {
            return NexApiRequestContext.Ensure(response);
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

        private static void WriteError(IOSHttpResponse response, HttpStatusCode status, string error, string message)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    error,
                    message,
                    correlation_id =
                        NexApiRequestContext.CurrentCorrelationId
                },
                s_Json);
        }
    }
}
