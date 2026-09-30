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

    public sealed class NexUserProvisionResult
    {
        public NexUserRecord User { get; }
        public bool AuthenticationInitialized { get; }
        public bool InventoryInitialized { get; }
        public bool HomeInitialized { get; }

        public bool Ready =>
            User != null &&
            AuthenticationInitialized &&
            InventoryInitialized;

        public NexUserProvisionResult(
            NexUserRecord user,
            bool authenticationInitialized,
            bool inventoryInitialized,
            bool homeInitialized)
        {
            User = user;
            AuthenticationInitialized = authenticationInitialized;
            InventoryInitialized = inventoryInitialized;
            HomeInitialized = homeInitialized;
        }
    }

    public interface INexUserService
    {
        NexUserRecord GetById(string principalId);
        NexUserRecord GetByName(string firstName, string lastName);
        IReadOnlyList<NexUserRecord> Search(string query, int limit);

        NexUserProvisionResult Create(
            string firstName,
            string lastName,
            string email,
            string password);

        NexUserRecord UpdateProfile(
            string principalId,
            string email,
            string userTitle,
            string userCountry);

        bool SetUserLevel(string principalId, int userLevel);
        bool SetPassword(string principalId, string password);
    }
}
