// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;

namespace NexVerse.Core.Identity
{
    public static class NexAccountStates
    {
        public const string Active = "active";
        public const string Locked = "locked";
        public const string Banned = "banned";
        public const string Deactivated = "deactivated";
        public const string Provisioning = "provisioning";
        public const string ProvisioningFailed = "provisioning_failed";

        public static bool IsAdministrativeState(string state)
        {
            return state == Active ||
                   state == Locked ||
                   state == Banned ||
                   state == Deactivated;
        }
    }

    public sealed class NexUserRecord
    {
        public string PrincipalId { get; }
        public string FirstName { get; }
        public string LastName { get; }
        public string Email { get; }
        public int UserLevel { get; }
        public int UserFlags { get; }
        public string UserTitle { get; }
        public string UserCountry { get; }
        public string DisplayName { get; }
        public bool IsDisplayNameDefault { get; }
        public int DisplayNameChanged { get; }
        public int DisplayNameNextUpdate { get; }
        public bool LocalToGrid { get; }
        public bool Active { get; }
        public string AccountState { get; }
        public string AccountStateReason { get; }
        public int AccountStateChanged { get; }
        public IReadOnlyList<string> Roles { get; }
        public IReadOnlyList<string> ExplicitScopes { get; }
        public int Created { get; }

        public NexUserRecord(
            string principalId,
            string firstName,
            string lastName,
            string email,
            int userLevel,
            int userFlags,
            string userTitle,
            string userCountry,
            string displayName,
            bool isDisplayNameDefault,
            int displayNameChanged,
            int displayNameNextUpdate,
            bool localToGrid,
            bool active,
            string accountState,
            string accountStateReason,
            int accountStateChanged,
            IReadOnlyList<string> roles,
            IReadOnlyList<string> explicitScopes,
            int created)
        {
            PrincipalId = principalId ?? string.Empty;
            FirstName = firstName ?? string.Empty;
            LastName = lastName ?? string.Empty;
            Email = email ?? string.Empty;
            UserLevel = userLevel;
            UserFlags = userFlags;
            UserTitle = userTitle ?? string.Empty;
            UserCountry = userCountry ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsDisplayNameDefault = isDisplayNameDefault;
            DisplayNameChanged = displayNameChanged;
            DisplayNameNextUpdate = displayNameNextUpdate;
            LocalToGrid = localToGrid;
            Active = active;
            AccountState = accountState ?? NexAccountStates.Active;
            AccountStateReason = accountStateReason ?? string.Empty;
            AccountStateChanged = accountStateChanged;
            Roles = roles ?? new string[0];
            ExplicitScopes = explicitScopes ?? new string[0];
            Created = created;
        }
    }

    public sealed class NexRegionRecord
    {
        public string RegionId { get; }
        public string Name { get; }
        public string ServerUri { get; }
        public int GridX { get; }
        public int GridY { get; }
        public int WorldX { get; }
        public int WorldY { get; }
        public int SizeX { get; }
        public int SizeY { get; }

        public NexRegionRecord(
            string regionId,
            string name,
            string serverUri,
            int gridX,
            int gridY,
            int worldX,
            int worldY,
            int sizeX,
            int sizeY)
        {
            RegionId = regionId ?? string.Empty;
            Name = name ?? string.Empty;
            ServerUri = serverUri ?? string.Empty;
            GridX = gridX;
            GridY = gridY;
            WorldX = worldX;
            WorldY = worldY;
            SizeX = sizeX;
            SizeY = sizeY;
        }
    }

    public sealed class NexUserProvisionResult
    {
        public NexUserRecord User { get; }
        public bool AuthenticationInitialized { get; }
        public bool InventoryInitialized { get; }
        public bool HomeInitialized { get; }
        public bool StartPositionInitialized { get; }
        public bool StateFinalized { get; }
        public NexRegionRecord HomeRegion { get; }

        public bool Ready =>
            User != null &&
            AuthenticationInitialized &&
            InventoryInitialized &&
            HomeInitialized &&
            StartPositionInitialized &&
            StateFinalized &&
            User.Active &&
            User.AccountState == NexAccountStates.Active;

        public NexUserProvisionResult(
            NexUserRecord user,
            bool authenticationInitialized,
            bool inventoryInitialized,
            bool homeInitialized,
            bool startPositionInitialized,
            bool stateFinalized,
            NexRegionRecord homeRegion)
        {
            User = user;
            AuthenticationInitialized = authenticationInitialized;
            InventoryInitialized = inventoryInitialized;
            HomeInitialized = homeInitialized;
            StartPositionInitialized = startPositionInitialized;
            StateFinalized = stateFinalized;
            HomeRegion = homeRegion;
        }
    }

    public interface INexUserService
    {
        NexUserRecord GetById(string principalId);
        NexUserRecord GetByName(string firstName, string lastName);
        IReadOnlyList<NexUserRecord> Search(string query, int limit);
        IReadOnlyList<NexRegionRecord> SearchHomeRegions(string query, int limit);
        NexRegionRecord ResolveHomeRegion(string regionId, string regionName);

        NexUserProvisionResult Create(
            string firstName,
            string lastName,
            string email,
            string password,
            string homeRegionId,
            float homeX,
            float homeY,
            float homeZ);

        NexUserRecord UpdateProfile(
            string principalId,
            string email,
            string userTitle,
            string userCountry);

        NexUserRecord SetDisplayName(
            string principalId,
            string displayName,
            bool bypassCooldown,
            out int retryAfterSeconds,
            out string error);

        bool VerifyPassword(string principalId, string password);
        bool SetUserLevel(string principalId, int userLevel);
        NexUserRecord SetAuthorization(
            string principalId,
            IEnumerable<string> roles,
            IEnumerable<string> scopes,
            bool allowPrivilegedGrant,
            out string error);
        bool SetPassword(string principalId, string password);
        NexUserRecord SetAccountState(string principalId, string state, string reason);
    }
}
