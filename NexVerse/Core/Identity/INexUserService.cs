// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;

namespace NexVerse.Core.Identity
{
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
        public bool LocalToGrid { get; }
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
            bool localToGrid,
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
            LocalToGrid = localToGrid;
            Created = created;
        }
    }

    public sealed class NexRegionRecord
    {
        public string RegionId { get; }
        public string Name { get; }
        public string ServerUri { get; }
        public int SizeX { get; }
        public int SizeY { get; }

        public NexRegionRecord(string regionId, string name, string serverUri, int sizeX, int sizeY)
        {
            RegionId = regionId ?? string.Empty;
            Name = name ?? string.Empty;
            ServerUri = serverUri ?? string.Empty;
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
        public NexRegionRecord HomeRegion { get; }

        public bool Ready =>
            User != null &&
            AuthenticationInitialized &&
            InventoryInitialized &&
            HomeInitialized &&
            StartPositionInitialized;

        public NexUserProvisionResult(
            NexUserRecord user,
            bool authenticationInitialized,
            bool inventoryInitialized,
            bool homeInitialized,
            bool startPositionInitialized,
            NexRegionRecord homeRegion)
        {
            User = user;
            AuthenticationInitialized = authenticationInitialized;
            InventoryInitialized = inventoryInitialized;
            HomeInitialized = homeInitialized;
            StartPositionInitialized = startPositionInitialized;
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

        bool SetUserLevel(string principalId, int userLevel);
        bool SetPassword(string principalId, string password);
    }
}
