// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.Economy;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Groups;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexGroupsApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly NexApiAuthenticator m_Authenticator;
        private readonly INexAuditSink m_Audit;
        private readonly GroupsService m_Groups;
        private readonly Func<NexEconomyService> m_Economy;

        public NexGroupsApi(
            NexApiAuthenticator authenticator,
            INexAuditSink audit,
            GroupsService groups,
            Func<NexEconomyService> economy)
        {
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_Groups = groups ?? throw new ArgumentNullException(nameof(groups));
            m_Economy = economy ?? throw new ArgumentNullException(nameof(economy));
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            string method = request?.HttpMethod ?? string.Empty;

            if (path == "/api/v1/groups")
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        string query = request?.QueryString?["q"] ?? string.Empty;
                        string requester = Requester(principal, request);
                        List<DirGroupsReplyData> groups =
                            m_Groups.FindGroups(requester, query);

                        WriteJson(response, new
                        {
                            groups = groups.Select(x => new
                            {
                                group_id = x.groupID.ToString(),
                                name = x.groupName,
                                members = x.members
                            }).ToArray(),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out NexPrincipal principal, out UserAccount account))
                        return;
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        UUID founder = Actor(principal, account, body);
                        UUID groupId =
                            m_Groups.CreateGroup(
                                founder.ToString(),
                                BodyString(body, "name"),
                                BodyString(body, "charter"),
                                BodyBool(body, "show_in_list", true),
                                BodyOptionalUuid(body, "insignia_id"),
                                BodyInt(body, "membership_fee", 0, 0, int.MaxValue),
                                BodyBool(body, "open_enrollment", false),
                                BodyBool(body, "allow_publish", false),
                                BodyBool(body, "mature_publish", false),
                                founder,
                                out string reason);

                        if (groupId.IsZero())
                            throw new InvalidOperationException(
                                string.IsNullOrWhiteSpace(reason)
                                    ? "Group creation failed."
                                    : reason);

                        ExtendedGroupRecord group =
                            m_Groups.GetGroupRecord(founder.ToString(), groupId);

                        EnsureGroupWallet(group);

                        Audit(principal, "group.created", groupId, response);

                        WriteJson(response, new
                        {
                            group = GroupPayload(group),
                            correlation_id = Correlation(response)
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                MethodNotAllowed(response, "GET or POST");
                return;
            }

            const string prefix = "/api/v1/groups/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                NotFound(response);
                return;
            }

            string relative = path.Substring(prefix.Length);
            string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 ||
                !UUID.TryParse(parts[0], out UUID groupId) ||
                groupId.IsZero())
            {
                WriteError(response, HttpStatusCode.BadRequest, "invalid_group_id", "A valid group UUID is required.");
                return;
            }

            if (parts.Length == 1)
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        ExtendedGroupRecord group =
                            RequireGroup(principal, request, groupId);

                        WriteJson(response, new
                        {
                            group = GroupPayload(group),
                            compatibility = CompatibilityPayload(groupId),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out NexPrincipal principal, out UserAccount account))
                        return;
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        UUID actor = Actor(principal, account, body);
                        ExtendedGroupRecord current =
                            m_Groups.GetGroupRecord(actor.ToString(), groupId) ??
                            throw new KeyNotFoundException("Group was not found.");

                        m_Groups.UpdateGroup(
                            actor.ToString(),
                            groupId,
                            body.TryGetProperty("charter", out _) ? BodyString(body, "charter") : current.Charter,
                            body.TryGetProperty("show_in_list", out _) ? BodyBool(body, "show_in_list", current.ShowInList) : current.ShowInList,
                            body.TryGetProperty("insignia_id", out _) ? BodyOptionalUuid(body, "insignia_id") : current.GroupPicture,
                            body.TryGetProperty("membership_fee", out _) ? BodyInt(body, "membership_fee", current.MembershipFee, 0, int.MaxValue) : current.MembershipFee,
                            body.TryGetProperty("open_enrollment", out _) ? BodyBool(body, "open_enrollment", current.OpenEnrollment) : current.OpenEnrollment,
                            body.TryGetProperty("allow_publish", out _) ? BodyBool(body, "allow_publish", current.AllowPublish) : current.AllowPublish,
                            body.TryGetProperty("mature_publish", out _) ? BodyBool(body, "mature_publish", current.MaturePublish) : current.MaturePublish);

                        ExtendedGroupRecord updated =
                            m_Groups.GetGroupRecord(actor.ToString(), groupId);

                        Audit(principal, "group.updated", groupId, response);

                        WriteJson(response, new
                        {
                            group = GroupPayload(updated),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out NexPrincipal principal, out UserAccount account))
                        return;

                    try
                    {
                        UUID actor =
                            account != null
                                ? account.PrincipalID
                                : QueryUuid(request, "actor_id");

                        if (!m_Groups.DeleteGroup(
                                actor.ToString(),
                                groupId,
                                out string reason))
                        {
                            throw new UnauthorizedAccessException(
                                string.IsNullOrWhiteSpace(reason)
                                    ? "Group deletion failed."
                                    : reason);
                        }

                        Audit(principal, "group.deleted", groupId, response);

                        WriteJson(response, new
                        {
                            deleted = true,
                            group_id = groupId.ToString(),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                MethodNotAllowed(response, "GET, PUT or DELETE");
                return;
            }

            string requester;
            NexPrincipal authPrincipal;
            UserAccount authAccount;

            if (parts[1].Equals("members", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out authAccount))
                        return;

                    try
                    {
                        requester = ActorForRead(authPrincipal, authAccount, request);
                        List<ExtendedGroupMembersData> members =
                            m_Groups.GetGroupMembers(requester, groupId);

                        WriteJson(response, new
                        {
                            members = members.Select(GroupsDataUtils.GroupMembersData).ToArray(),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length == 3 &&
                    UUID.TryParse(parts[2], out UUID memberId))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                        return;

                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryBody(request, response, out JsonElement body))
                            return;

                        try
                        {
                            UUID actor = Actor(authPrincipal, authAccount, body);
                            UUID roleId = BodyOptionalUuid(body, "role_id");

                            if (!m_Groups.AddAgentToGroup(
                                    actor.ToString(),
                                    memberId.ToString(),
                                    groupId,
                                    roleId,
                                    BodyString(body, "token"),
                                    out string reason))
                            {
                                throw new InvalidOperationException(
                                    string.IsNullOrWhiteSpace(reason)
                                        ? "Group membership could not be added."
                                        : reason);
                            }

                            Audit(authPrincipal, "group.member.added", groupId, response);
                            WriteJson(response, new { added = true, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }

                    if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            UUID actor =
                                authAccount != null
                                    ? authAccount.PrincipalID
                                    : QueryUuid(request, "actor_id");

                            if (!m_Groups.RemoveAgentFromGroup(
                                    actor.ToString(),
                                    memberId.ToString(),
                                    groupId))
                            {
                                throw new UnauthorizedAccessException("Member removal was rejected.");
                            }

                            Audit(authPrincipal, "group.member.removed", groupId, response);
                            WriteJson(response, new { removed = true, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }
                }
            }

            if (parts[1].Equals("roles", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out authAccount))
                        return;

                    try
                    {
                        requester = ActorForRead(authPrincipal, authAccount, request);

                        WriteJson(response, new
                        {
                            roles = m_Groups.GetGroupRoles(requester, groupId)
                                .Select(GroupsDataUtils.GroupRolesData)
                                .ToArray(),
                            role_members = m_Groups.GetGroupRoleMembers(requester, groupId)
                                .Select(GroupsDataUtils.GroupRoleMembersData)
                                .ToArray(),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length == 2 && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                        return;
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        UUID actor = Actor(authPrincipal, authAccount, body);
                        UUID roleId = BodyOptionalUuid(body, "role_id");
                        if (roleId.IsZero())
                            roleId = UUID.Random();

                        if (!m_Groups.AddGroupRole(
                                actor.ToString(),
                                groupId,
                                roleId,
                                BodyString(body, "name"),
                                BodyString(body, "description"),
                                BodyString(body, "title"),
                                BodyUlong(body, "powers", 0),
                                out string reason))
                        {
                            throw new UnauthorizedAccessException(
                                string.IsNullOrWhiteSpace(reason)
                                    ? "Role creation was rejected."
                                    : reason);
                        }

                        Audit(authPrincipal, "group.role.created", groupId, response);
                        WriteJson(response, new
                        {
                            role_id = roleId.ToString(),
                            correlation_id = Correlation(response)
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length >= 3 &&
                    UUID.TryParse(parts[2], out UUID roleId))
                {
                    if (parts.Length == 3 &&
                        (method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                            return;

                        try
                        {
                            if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!TryBody(request, response, out JsonElement body))
                                    return;

                                UUID actor = Actor(authPrincipal, authAccount, body);
                                bool updated =
                                    m_Groups.UpdateGroupRole(
                                        actor.ToString(),
                                        groupId,
                                        roleId,
                                        BodyString(body, "name"),
                                        BodyString(body, "description"),
                                        BodyString(body, "title"),
                                        BodyUlong(body, "powers", 0));

                                if (!updated)
                                    throw new UnauthorizedAccessException("Role update was rejected.");

                                Audit(authPrincipal, "group.role.updated", groupId, response);
                                WriteJson(response, new { updated = true, correlation_id = Correlation(response) });
                                return;
                            }

                            UUID deleteActor =
                                authAccount != null
                                    ? authAccount.PrincipalID
                                    : QueryUuid(request, "actor_id");

                            m_Groups.RemoveGroupRole(
                                deleteActor.ToString(),
                                groupId,
                                roleId);

                            Audit(authPrincipal, "group.role.deleted", groupId, response);
                            WriteJson(response, new { deleted = true, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }

                    if (parts.Length == 5 &&
                        parts[3].Equals("members", StringComparison.OrdinalIgnoreCase) &&
                        UUID.TryParse(parts[4], out UUID roleMember) &&
                        (method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                            return;

                        try
                        {
                            UUID actor =
                                authAccount != null
                                    ? authAccount.PrincipalID
                                    : QueryUuid(request, "actor_id");

                            bool ok =
                                method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                                    ? m_Groups.AddAgentToGroupRole(
                                        actor.ToString(),
                                        roleMember.ToString(),
                                        groupId,
                                        roleId)
                                    : m_Groups.RemoveAgentFromGroupRole(
                                        actor.ToString(),
                                        roleMember.ToString(),
                                        groupId,
                                        roleId);

                            if (!ok)
                                throw new UnauthorizedAccessException("Role membership change was rejected.");

                            Audit(authPrincipal, "group.role_membership.changed", groupId, response);
                            WriteJson(response, new { success = true, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }
                }
            }

            if (parts[1].Equals("invites", StringComparison.OrdinalIgnoreCase))
            {
                if (!Authenticate(request, response,
                        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                            ? NexScopes.GroupsRead
                            : NexScopes.GroupsManage,
                        out authPrincipal,
                        out authAccount))
                {
                    return;
                }

                if (parts.Length == 2 &&
                    method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        UUID actor = Actor(authPrincipal, authAccount, body);
                        UUID inviteId = UUID.Random();
                        bool ok =
                            m_Groups.AddAgentToGroupInvite(
                                actor.ToString(),
                                inviteId,
                                groupId,
                                BodyOptionalUuid(body, "role_id"),
                                BodyUuid(body, "agent_id").ToString());

                        if (!ok)
                            throw new UnauthorizedAccessException("Group invitation was rejected.");

                        Audit(authPrincipal, "group.invite.created", groupId, response);
                        WriteJson(response, new
                        {
                            invite_id = inviteId.ToString(),
                            correlation_id = Correlation(response)
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length == 3 &&
                    UUID.TryParse(parts[2], out UUID inviteId))
                {
                    if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            GroupInviteInfo invite =
                                m_Groups.GetAgentToGroupInvite(
                                    Requester(authPrincipal, request),
                                    inviteId) ??
                                throw new KeyNotFoundException("Group invitation was not found.");

                            WriteJson(response, new
                            {
                                invite = GroupsDataUtils.GroupInviteInfo(invite),
                                correlation_id = Correlation(response)
                            });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }

                    if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        m_Groups.RemoveAgentToGroupInvite(
                            Requester(authPrincipal, request),
                            inviteId);
                        Audit(authPrincipal, "group.invite.deleted", groupId, response);
                        WriteJson(response, new { deleted = true, correlation_id = Correlation(response) });
                        return;
                    }
                }
            }

            if (parts[1].Equals("notices", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out authAccount))
                        return;

                    try
                    {
                        requester = ActorForRead(authPrincipal, authAccount, request);
                        WriteJson(response, new
                        {
                            notices = m_Groups.GetGroupNotices(requester, groupId)
                                .Select(GroupsDataUtils.GroupNoticeData)
                                .ToArray(),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length == 2 && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                        return;
                    if (!TryBody(request, response, out JsonElement body))
                        return;

                    try
                    {
                        UUID actor = Actor(authPrincipal, authAccount, body);
                        UUID noticeId = UUID.Random();

                        bool ok =
                            m_Groups.AddGroupNotice(
                                actor.ToString(),
                                groupId,
                                noticeId,
                                BodyString(body, "from_name"),
                                BodyString(body, "subject"),
                                BodyString(body, "message"),
                                BodyBool(body, "has_attachment", false),
                                (byte)BodyInt(body, "attachment_type", 0, 0, 255),
                                BodyString(body, "attachment_name"),
                                BodyOptionalUuid(body, "attachment_item_id"),
                                BodyString(body, "attachment_owner_id"));

                        if (!ok)
                            throw new UnauthorizedAccessException("Group notice was rejected.");

                        Audit(authPrincipal, "group.notice.created", groupId, response);
                        WriteJson(response, new
                        {
                            notice_id = noticeId.ToString(),
                            correlation_id = Correlation(response)
                        }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }
            }

            if (parts[1].Equals("bans", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 &&
                    method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out authAccount))
                        return;

                    try
                    {
                        requester = ActorForRead(authPrincipal, authAccount, request);
                        WriteJson(response, new
                        {
                            bans = m_Groups.GetGroupBans(requester, groupId),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (parts.Length == 3 &&
                    UUID.TryParse(parts[2], out UUID bannedAgent))
                {
                    if (!Authenticate(request, response, NexScopes.GroupsManage, out authPrincipal, out authAccount))
                        return;

                    if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryBody(request, response, out JsonElement body))
                            return;

                        try
                        {
                            UUID actor = Actor(authPrincipal, authAccount, body);
                            if (!m_Groups.AddGroupBan(
                                    actor.ToString(),
                                    groupId,
                                    bannedAgent.ToString(),
                                    BodyString(body, "reason"),
                                    out string error))
                            {
                                throw new UnauthorizedAccessException(error);
                            }

                            Audit(authPrincipal, "group.ban.added", groupId, response);
                            WriteJson(response, new { banned = true, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }

                    if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            UUID actor =
                                authAccount != null
                                    ? authAccount.PrincipalID
                                    : QueryUuid(request, "actor_id");

                            bool removed =
                                m_Groups.RemoveGroupBan(
                                    actor.ToString(),
                                    groupId,
                                    bannedAgent.ToString(),
                                    out string error);

                            if (!removed && !string.IsNullOrWhiteSpace(error))
                                throw new UnauthorizedAccessException(error);

                            Audit(authPrincipal, "group.ban.removed", groupId, response);
                            WriteJson(response, new { removed, correlation_id = Correlation(response) });
                        }
                        catch (Exception e)
                        {
                            WriteFailure(response, e);
                        }
                        return;
                    }
                }
            }

            if (parts[1].Equals("accounting", StringComparison.OrdinalIgnoreCase) &&
                parts.Length == 2)
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out authAccount))
                    return;

                try
                {
                    ExtendedGroupRecord group =
                        RequireGroup(authPrincipal, request, groupId);
                    NexEconomyService economy =
                        m_Economy() ??
                        throw new InvalidOperationException("NV$ economy is unavailable.");

                    economy.EnsureWalletAccount(
                        groupId.Guid,
                        NexLedgerAccountClass.Group,
                        group.GroupName);

                    NexVirtualBankAccount bank =
                        economy.EnsureVirtualBankAccount(
                            groupId.Guid);

                    WriteJson(response, new
                    {
                        group_id = groupId.ToString(),
                        currency = NexLedgerCurrency.Code,
                        balance = economy.GetBalance(groupId.Guid),
                        nvban = bank.Identifier,
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            if (parts[1].Equals("capabilities", StringComparison.OrdinalIgnoreCase) &&
                parts.Length == 2)
            {
                if (!RequireMethod(method, "GET", response))
                    return;
                if (!Authenticate(request, response, NexScopes.GroupsRead, out authPrincipal, out _))
                    return;

                try
                {
                    ExtendedGroupRecord group =
                        RequireGroup(authPrincipal, request, groupId);

                    WriteJson(response, new
                    {
                        group_id = group.GroupID.ToString(),
                        chat = new
                        {
                            enabled = true,
                            session_id = group.GroupID.ToString(),
                            transport = "GroupsMessagingModule"
                        },
                        voice = new
                        {
                            policy_available = true,
                            channel_id = "group:" + group.GroupID,
                            media_transport = "NexVoice (Roadmap 13)"
                        },
                        land = new
                        {
                            group_land_semantics = true,
                            authority = "native parcel GroupID and role powers"
                        },
                        objects = new
                        {
                            group_owned_objects = true,
                            authority = "native object GroupID/ownership semantics"
                        },
                        hypergrid = new
                        {
                            connector_preserved = true,
                            transport = "GroupsServiceHGConnector"
                        },
                        correlation_id = Correlation(response)
                    });
                }
                catch (Exception e)
                {
                    WriteFailure(response, e);
                }
                return;
            }

            NotFound(response);
        }

        private ExtendedGroupRecord RequireGroup(
            NexPrincipal principal,
            IOSHttpRequest request,
            UUID groupId)
        {
            ExtendedGroupRecord group =
                m_Groups.GetGroupRecord(
                    Requester(principal, request),
                    groupId);

            return group ??
                throw new KeyNotFoundException("Group was not found.");
        }

        private void EnsureGroupWallet(
            ExtendedGroupRecord group)
        {
            NexEconomyService economy =
                m_Economy();

            if (economy == null || group == null)
                return;

            economy.EnsureWalletAccount(
                group.GroupID.Guid,
                NexLedgerAccountClass.Group,
                group.GroupName);
            economy.EnsureVirtualBankAccount(
                group.GroupID.Guid);
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
            {
                return true;
            }

            response.AddHeader("WWW-Authenticate", "Bearer");
            WriteError(response, (HttpStatusCode)statusCode, error, "Authentication or " + scope + " authorization is required.");
            return false;
        }

        private static UUID Actor(
            NexPrincipal principal,
            UserAccount account,
            JsonElement body)
        {
            if (account != null)
                return account.PrincipalID;

            UUID actor = BodyOptionalUuid(body, "actor_id");
            if (actor.IsZero() && principal.HasScope(NexScopes.AdminAll))
                throw new ArgumentException("actor_id is required for administrator/service group mutations.");
            return actor;
        }

        private static string ActorForRead(
            NexPrincipal principal,
            UserAccount account,
            IOSHttpRequest request) =>
            account != null
                ? account.PrincipalID.ToString()
                : Requester(principal, request);

        private static string Requester(
            NexPrincipal principal,
            IOSHttpRequest request)
        {
            string raw =
                (request?.QueryString?["requester_id"] ?? string.Empty).Trim();

            if (UUID.TryParse(raw, out UUID id) && !id.IsZero())
                return id.ToString();

            return principal.HasScope(NexScopes.AdminAll)
                ? UUID.Zero.ToString()
                : principal.Subject;
        }

        private void Audit(
            NexPrincipal principal,
            string action,
            UUID groupId,
            IOSHttpResponse response)
        {
            m_Audit.Record(
                new NexAuditEvent(
                    principal.Subject,
                    action,
                    "group:" + groupId,
                    Correlation(response),
                    new Dictionary<string, string>()));
        }

        private static object GroupPayload(
            ExtendedGroupRecord group) =>
            GroupsDataUtils.GroupRecord(group);

        private static object CompatibilityPayload(UUID groupId) =>
            new
            {
                group_id = groupId.ToString(),
                viewer_stack = "Groups V2",
                parity_mode = "NexGroups facade with legacy-compatible transport",
                chat = true,
                notices = true,
                roles = true,
                invitations = true,
                bans = true,
                accounting = true,
                hypergrid_connector_preserved = true
            };

        private static bool TryBody(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonElement body)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(request.InputStream);
                body = document.RootElement.Clone();
                if (body.ValueKind == JsonValueKind.Object)
                    return true;
            }
            catch
            {
            }

            body = default;
            WriteError(response, HttpStatusCode.BadRequest, "invalid_json", "A valid JSON object is required.");
            return false;
        }

        private static string BodyString(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return (value.GetString() ?? string.Empty).Trim();
        }

        private static bool BodyBool(
            JsonElement body,
            string name,
            bool defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            throw new ArgumentException(name + " must be boolean.");
        }

        private static int BodyInt(
            JsonElement body,
            string name,
            int defaultValue,
            int min,
            int max)
        {
            if (!body.TryGetProperty(name, out JsonElement value))
                return defaultValue;
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int parsed) ||
                parsed < min ||
                parsed > max)
            {
                throw new ArgumentException(name + " contains an invalid integer.");
            }

            return parsed;
        }

        private static ulong BodyUlong(
            JsonElement body,
            string name,
            ulong defaultValue)
        {
            if (!body.TryGetProperty(name, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetUInt64(out ulong numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String &&
                ulong.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out ulong text))
            {
                return text;
            }

            throw new ArgumentException(name + " must be an unsigned integer.");
        }

        private static UUID BodyUuid(JsonElement body, string name)
        {
            UUID id = BodyOptionalUuid(body, name);
            if (id.IsZero())
                throw new ArgumentException(name + " must be a non-zero UUID.");
            return id;
        }

        private static UUID BodyOptionalUuid(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return UUID.Zero;
            }

            if (value.ValueKind != JsonValueKind.String ||
                !UUID.TryParse(value.GetString(), out UUID id))
            {
                throw new ArgumentException(name + " must be a UUID.");
            }

            return id;
        }

        private static UUID QueryUuid(
            IOSHttpRequest request,
            string name)
        {
            string raw = (request?.QueryString?[name] ?? string.Empty).Trim();
            if (!UUID.TryParse(raw, out UUID id) || id.IsZero())
                throw new ArgumentException(name + " must be a non-zero UUID.");
            return id;
        }

        private static bool RequireMethod(
            string actual,
            string expected,
            IOSHttpResponse response)
        {
            if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;
            MethodNotAllowed(response, expected);
            return false;
        }

        private static string Correlation(
            IOSHttpResponse response) =>
            NexApiRequestContext.Ensure(response);

        private static void WriteFailure(
            IOSHttpResponse response,
            Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                WriteError(response, HttpStatusCode.Forbidden, "group_forbidden", exception.Message);
                return;
            }

            if (exception is KeyNotFoundException)
            {
                WriteError(response, HttpStatusCode.NotFound, "group_not_found", exception.Message);
                return;
            }

            if (exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                WriteError(response, HttpStatusCode.BadRequest, "group_validation_failed", exception.Message);
                return;
            }

            WriteError(response, HttpStatusCode.InternalServerError, "group_internal_error", "The NexGroups operation failed.");
        }

        private static void MethodNotAllowed(
            IOSHttpResponse response,
            string expected) =>
            WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", expected + " is required.");

        private static void NotFound(
            IOSHttpResponse response) =>
            WriteError(response, HttpStatusCode.NotFound, "not_found", "The requested NexGroups endpoint was not found.");

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string message) =>
            WriteJson(response, new
            {
                error,
                message,
                correlation_id = Correlation(response)
            }, status);

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer =
                JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    s_Json);
        }
    }
}
