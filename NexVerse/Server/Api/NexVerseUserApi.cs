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
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

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

            if (!account.LoginAllowed)
            {
                statusCode = (int)HttpStatusCode.Forbidden;
                error = "account_blocked";
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

        public IReadOnlyList<NexRegionRecord> SearchHomeRegions(string query, int limit)
        {
            if (m_Grid == null)
                return Array.Empty<NexRegionRecord>();

            int safeLimit = Math.Max(1, Math.Min(limit, 100));
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
            account.NexVerseStateChanged = OpenSim.Framework.Util.UnixTimeSinceEpoch();

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

        public bool SetUserLevel(string principalId, int userLevel)
        {
            if (!UUID.TryParse(principalId, out UUID id))
                return false;

            UserAccount account = m_UserAccounts.GetUserAccount(UUID.Zero, id);
            if (account == null)
                return false;

            account.UserLevel = userLevel;
            bool stored = m_UserAccounts.StoreUserAccount(account);
            if (stored)
                m_UserAccounts.InvalidateCache(id);

            return stored;
        }

        public bool SetPassword(string principalId, string password)
        {
            return UUID.TryParse(principalId, out UUID id) &&
                   m_UserAccounts.GetUserAccount(UUID.Zero, id) != null &&
                   m_Authentication.SetPassword(id, password);
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
            account.NexVerseStateChanged = OpenSim.Framework.Util.UnixTimeSinceEpoch();
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

            return new NexRegionRecord(
                region.RegionID.ToString(),
                region.RegionName,
                region.ServerURI,
                region.RegionSizeX,
                region.RegionSizeY);
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
                account.Active,
                account.NexVerseState,
                account.NexVerseStateReason,
                account.NexVerseStateChanged,
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

            if (string.Equals(path, "/api/v1/regions", StringComparison.OrdinalIgnoreCase))
            {
                HandleRegionSearch(request, response);
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

                if (parts.Length == 2 && string.Equals(parts[1], "password", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSetPassword(request, response, parts[0]);
                    return;
                }
            }

            WriteError(response, HttpStatusCode.NotFound, "not_found", "Unknown NexVerse API endpoint.");
        }

        private void HandleRegionSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            if (!Authenticate(request, response, NexScopes.AdminAll, out NexPrincipal _, out UserAccount _))
                return;

            string query = request.QueryString?["q"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(query) && query.Trim().Length < 2)
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_query", "Region search query must be empty or contain at least two characters.");
                return;
            }

            IReadOnlyList<NexRegionRecord> regions = m_Users.SearchHomeRegions(query, 50);
            WriteJson(response, new
            {
                count = regions.Count,
                regions = regions.Select(RegionPayload).ToArray()
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

            IReadOnlyList<NexUserRecord> users = m_Users.Search(query, 50);
            WriteJson(response, new
            {
                count = users.Count,
                users = users.Select(UserPayload).ToArray()
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

                if (m_Users.GetByName(firstName, lastName) != null)
                {
                    WriteError(response, HttpStatusCode.Conflict, "user_exists", "A user with this name already exists.");
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

                NexUserRecord updated = m_Users.UpdateProfile(principalId, email, userTitle, userCountry);
                if (updated == null)
                {
                    WriteError(response, HttpStatusCode.InternalServerError, "user_update_failed", "The user account could not be updated.");
                    return;
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

        private static Dictionary<string, object> RegionPayload(NexRegionRecord region)
        {
            if (region == null)
                return null;

            return new Dictionary<string, object>
            {
                ["region_id"] = region.RegionId,
                ["name"] = region.Name,
                ["server_uri"] = region.ServerUri,
                ["size_x"] = region.SizeX,
                ["size_y"] = region.SizeY
            };
        }

        private static Dictionary<string, object> UserPayload(NexUserRecord user)
        {
            return new Dictionary<string, object>
            {
                ["principal_id"] = user.PrincipalId,
                ["first_name"] = user.FirstName,
                ["last_name"] = user.LastName,
                ["email"] = user.Email,
                ["user_level"] = user.UserLevel,
                ["user_flags"] = user.UserFlags,
                ["user_title"] = user.UserTitle,
                ["user_country"] = user.UserCountry,
                ["local_to_grid"] = user.LocalToGrid,
                ["active"] = user.Active,
                ["account_state"] = user.AccountState,
                ["account_state_reason"] = user.AccountStateReason,
                ["account_state_changed"] = user.AccountStateChanged,
                ["created"] = user.Created
            };
        }

        private static void WriteUser(IOSHttpResponse response, NexUserRecord user)
        {
            WriteJson(response, UserPayload(user));
        }

        private static string AddCorrelation(IOSHttpResponse response)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            response.AddHeader("X-NexVerse-Api-Version", Core.NexVersePlatform.ApiVersion);
            response.AddHeader("X-Correlation-Id", correlationId);
            return correlationId;
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
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(new { error, message }, s_Json);
        }
    }
}
