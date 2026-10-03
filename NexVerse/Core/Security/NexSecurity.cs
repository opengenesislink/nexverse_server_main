// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Security
{
    public static class NexScopes
    {
        public const string AdminAll = "admin:*";
        public const string OpenId = "openid";
        public const string Profile = "profile";
        public const string ProfileRead = "profile:read";
        public const string ProfileWrite = "profile:write";
        public const string RelationshipsRead = "relationships:read";
        public const string RelationshipsWrite = "relationships:write";
        public const string SecurityManage = "security:manage";
        public const string OfflineAccess = "offline_access";
        public const string UsersRead = "users:read";
        public const string UsersWrite = "users:write";
        public const string InventoryRead = "inventory:read";
        public const string InventoryWrite = "inventory:write";
        public const string FriendsManage = "friends:manage";
        public const string RegionsRead = "regions:read";
        public const string RegionsManage = "regions:manage";
        public const string SimulatorsRead = "simulators:read";
        public const string SimulatorsManage = "simulators:manage";
        public const string StatisticsRead = "statistics:read";
        public const string EstatesRead = "estates:read";
        public const string EstatesManage = "estates:manage";
        public const string EconomyRead = "economy:read";
        public const string EconomyTransfer = "economy:transfer";
    }

    public sealed class NexPrincipal
    {
        private readonly HashSet<string> m_Scopes;

        public static NexPrincipal Anonymous { get; } =
            new NexPrincipal("anonymous", Array.Empty<string>(), false);

        public string Subject { get; }
        public bool IsAuthenticated { get; }
        public IReadOnlyCollection<string> Scopes => m_Scopes;

        public NexPrincipal(string subject, IEnumerable<string> scopes, bool isAuthenticated = true)
        {
            Subject = string.IsNullOrWhiteSpace(subject) ? "unknown" : subject;
            IsAuthenticated = isAuthenticated;
            m_Scopes = new HashSet<string>(scopes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool HasScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                return false;

            if (m_Scopes.Contains("*") || m_Scopes.Contains(NexScopes.AdminAll) || m_Scopes.Contains(scope))
                return true;

            int separator = scope.IndexOf(':');
            if (separator > 0)
                return m_Scopes.Contains(scope.Substring(0, separator) + ":*");

            return false;
        }
    }

    public interface INexAuthorizationService
    {
        bool IsAllowed(NexPrincipal principal, string requiredScope);
    }

    public sealed class NexAuthorizationService : INexAuthorizationService
    {
        public bool IsAllowed(NexPrincipal principal, string requiredScope)
        {
            return principal != null && principal.IsAuthenticated && principal.HasScope(requiredScope);
        }
    }
}
