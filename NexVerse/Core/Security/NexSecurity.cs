// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;

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
        public const string GroupsRead = "groups:read";
        public const string GroupsManage = "groups:manage";
        public const string ExperiencesRead = "experiences:read";
        public const string ExperiencesManage = "experiences:manage";
        public const string ExperiencesScript = "experiences:script";
        // Dedicated trusted simulator key; NEVER grant to resident/OAuth clients.
        public const string ExperiencesViewerPermissions = "experiences:viewer:permissions";
        public const string DiscoveryRead = "discovery:read";
        public const string DiscoverySubmit = "discovery:submit";
        public const string DiscoveryManage = "discovery:manage";
    }


    public static class NexRoles
    {
        public const string Resident = "resident";
        public const string Support = "support";
        public const string Moderator = "moderator";
        public const string RegionManager = "region_manager";
        public const string Administrator = "administrator";

        public static readonly string[] All =
        {
            Resident,
            Support,
            Moderator,
            RegionManager,
            Administrator
        };
    }

    public static class NexAuthorizationPolicy
    {
        private static readonly HashSet<string> s_AssignableScopes =
            new HashSet<string>(
                new[]
                {
                    NexScopes.UsersRead,
                    NexScopes.UsersWrite,
                    NexScopes.ProfileRead,
                    NexScopes.ProfileWrite,
                    NexScopes.RelationshipsRead,
                    NexScopes.RelationshipsWrite,
                    NexScopes.InventoryRead,
                    NexScopes.InventoryWrite,
                    NexScopes.FriendsManage,
                    NexScopes.RegionsRead,
                    NexScopes.RegionsManage,
                    NexScopes.SimulatorsRead,
                    NexScopes.SimulatorsManage,
                    NexScopes.StatisticsRead,
                    NexScopes.EstatesRead,
                    NexScopes.EstatesManage,
                    NexScopes.EconomyRead,
                    NexScopes.EconomyTransfer,
                    NexScopes.GroupsRead,
                    NexScopes.GroupsManage,
                    NexScopes.ExperiencesRead,
                    NexScopes.ExperiencesManage,
                    NexScopes.ExperiencesScript,
                    NexScopes.ExperiencesViewerPermissions,
                    NexScopes.DiscoveryRead,
                    NexScopes.DiscoverySubmit,
                    NexScopes.DiscoveryManage,
                    NexScopes.SecurityManage,
                    NexScopes.AdminAll
                },
                StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<string> AssignableScopes =>
            s_AssignableScopes
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public static string[] SplitStoredValues(string raw)
        {
            return (raw ?? string.Empty)
                .Split(
                    new[] { ' ', ',', ';', '\r', '\n', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string JoinStoredValues(IEnumerable<string> values)
        {
            return string.Join(
                " ",
                (values ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        public static bool TryNormalizeAssignment(
            IEnumerable<string> roles,
            IEnumerable<string> scopes,
            bool allowPrivilegedGrant,
            out string[] normalizedRoles,
            out string[] normalizedScopes,
            out string error)
        {
            normalizedRoles =
                (roles ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            if (normalizedRoles.Length == 0)
                normalizedRoles = new[] { NexRoles.Resident };

            foreach (string role in normalizedRoles)
            {
                if (!NexRoles.All.Contains(
                        role,
                        StringComparer.OrdinalIgnoreCase))
                {
                    normalizedScopes = Array.Empty<string>();
                    error = "unknown_role";
                    return false;
                }

                if (!allowPrivilegedGrant &&
                    string.Equals(
                        role,
                        NexRoles.Administrator,
                        StringComparison.OrdinalIgnoreCase))
                {
                    normalizedScopes = Array.Empty<string>();
                    error = "administrator_role_requires_admin";
                    return false;
                }
            }

            normalizedScopes =
                (scopes ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            foreach (string scope in normalizedScopes)
            {
                if (!s_AssignableScopes.Contains(scope) ||
                    string.Equals(scope, "*", StringComparison.Ordinal))
                {
                    error = "unsupported_scope";
                    return false;
                }

                if (!allowPrivilegedGrant &&
                    (string.Equals(
                         scope,
                         NexScopes.AdminAll,
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         scope,
                         NexScopes.SecurityManage,
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         scope,
                         NexScopes.ExperiencesViewerPermissions,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    error = "privileged_scope_requires_admin";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static string[] GetEffectiveScopes(
            int userLevel,
            int adminMinimumLevel,
            IEnumerable<string> roles,
            IEnumerable<string> explicitScopes)
        {
            HashSet<string> scopes =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    NexScopes.UsersRead,
                    NexScopes.UsersWrite,
                    // Resident self-service: each endpoint still checks ownership
                    // or group membership/powers in the authoritative service.
                    NexScopes.ProfileRead,
                    NexScopes.ProfileWrite,
                    NexScopes.RelationshipsRead,
                    NexScopes.RelationshipsWrite,
                    NexScopes.GroupsRead,
                    NexScopes.GroupsManage
                };

            foreach (string role in roles ?? Array.Empty<string>())
            {
                foreach (string scope in GetRoleScopes(role))
                    scopes.Add(scope);
            }

            foreach (string scope in explicitScopes ?? Array.Empty<string>())
            {
                if (s_AssignableScopes.Contains(scope))
                    scopes.Add(scope);
            }

            if (userLevel >= adminMinimumLevel)
                scopes.Add(NexScopes.AdminAll);

            return scopes
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string[] GetRoleScopes(string role)
        {
            if (string.Equals(role, NexRoles.Support, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    NexScopes.UsersRead,
                    NexScopes.RegionsRead,
                    NexScopes.SimulatorsRead,
                    NexScopes.StatisticsRead,
                    NexScopes.EstatesRead
                };
            }

            if (string.Equals(role, NexRoles.Moderator, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    NexScopes.UsersRead,
                    NexScopes.RegionsRead,
                    NexScopes.SimulatorsRead,
                    NexScopes.StatisticsRead,
                    NexScopes.EstatesRead,
                    NexScopes.GroupsRead,
                    NexScopes.ExperiencesRead,
                    NexScopes.DiscoveryRead,
                    NexScopes.DiscoveryManage
                };
            }

            if (string.Equals(role, NexRoles.RegionManager, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    NexScopes.UsersRead,
                    NexScopes.RegionsRead,
                    NexScopes.RegionsManage,
                    NexScopes.SimulatorsRead,
                    NexScopes.SimulatorsManage,
                    NexScopes.StatisticsRead,
                    NexScopes.EstatesRead,
                    NexScopes.EstatesManage,
                    NexScopes.DiscoveryRead
                };
            }

            if (string.Equals(role, NexRoles.Administrator, StringComparison.OrdinalIgnoreCase))
                return new[] { NexScopes.AdminAll };

            return Array.Empty<string>();
        }
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
