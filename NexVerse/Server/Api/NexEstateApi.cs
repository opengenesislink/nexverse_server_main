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
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    internal sealed class NexEstateApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly IEstateDataService m_Estates;
        private readonly IGridService m_Grid;
        private readonly IUserAccountService m_UserAccounts;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_Audit;

        public NexEstateApi(
            IEstateDataService estates,
            IGridService grid,
            IUserAccountService userAccounts,
            NexApiAuthenticator authenticator,
            INexEventBus eventBus,
            INexAuditSink audit)
        {
            m_Estates = estates;
            m_Grid = grid;
            m_UserAccounts = userAccounts;
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_EventBus =
                eventBus ??
                throw new ArgumentNullException(nameof(eventBus));
            m_Audit =
                audit ??
                NullNexAuditSink.Instance;
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (m_Estates == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "estate_data_unavailable",
                    "The Estate data service is not configured for the NexVerse World API.");
                return;
            }

            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');

            if (string.Equals(
                    path,
                    "/api/v1/estates",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (IsMethod(request, "GET"))
                {
                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.EstatesRead,
                            out NexPrincipal _))
                    {
                        return;
                    }

                    HandleList(
                        request,
                        response);
                    return;
                }

                if (IsMethod(request, "POST"))
                {
                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.EstatesManage,
                            out NexPrincipal principal))
                    {
                        return;
                    }

                    HandleCreate(
                        request,
                        response,
                        principal);
                    return;
                }

                WriteMethodError(
                    response,
                    "GET or POST is required.");
                return;
            }

            const string prefix =
                "/api/v1/estates/";

            if (!path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "not_found",
                    "Unknown NexVerse Estate endpoint.");
                return;
            }

            string relative =
                path.Substring(
                    prefix.Length);

            string[] parts =
                relative.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 ||
                !int.TryParse(
                    parts[0],
                    out int estateId) ||
                estateId <= 0)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_estate_id",
                    "A positive Estate ID is required.");
                return;
            }

            if (parts.Length == 1)
            {
                if (IsMethod(request, "GET"))
                {
                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.EstatesRead,
                            out NexPrincipal _))
                    {
                        return;
                    }

                    HandleGet(
                        response,
                        estateId);
                    return;
                }

                if (IsMethod(request, "DELETE"))
                {
                    if (!Authenticate(request, response, NexScopes.EstatesManage, out NexPrincipal principal)) return;
                    HandleDelete(response, estateId, principal);
                    return;
                }

                if (IsMethod(request, "PATCH"))
                {
                    if (!Authenticate(
                            request,
                            response,
                            NexScopes.EstatesManage,
                            out NexPrincipal principal))
                    {
                        return;
                    }

                    HandleUpdate(
                        request,
                        response,
                        estateId,
                        principal);
                    return;
                }

                WriteMethodError(
                    response,
                    "GET, PATCH or DELETE is required.");
                return;
            }

            if (parts.Length == 2 &&
                string.Equals(
                    parts[1],
                    "management",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(request, "GET"))
                {
                    WriteMethodError(
                        response,
                        "GET is required.");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EstatesManage,
                        out NexPrincipal _))
                {
                    return;
                }

                HandleManagement(
                    response,
                    estateId);
                return;
            }

            if (parts.Length == 3 &&
                string.Equals(
                    parts[1],
                    "regions",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMethod(request, "PUT"))
                {
                    WriteMethodError(
                        response,
                        "PUT is required.");
                    return;
                }

                if (!UUID.TryParse(
                        parts[2],
                        out UUID regionId) ||
                    regionId.IsZero())
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_region_id",
                        "A non-zero region UUID is required.");
                    return;
                }

                if (!Authenticate(
                        request,
                        response,
                        NexScopes.EstatesManage,
                        out NexPrincipal principal))
                {
                    return;
                }

                HandleRegionAssignment(
                    response,
                    estateId,
                    regionId,
                    principal);
                return;
            }

            WriteError(
                response,
                HttpStatusCode.NotFound,
                "not_found",
                "Unknown NexVerse Estate endpoint.");
        }

        private void HandleList(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string query =
                (request.QueryString?["q"] ??
                 string.Empty)
                    .Trim();

            if (query.Length == 1)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_query",
                    "Estate search query must be empty or contain at least two characters.");
                return;
            }

            UUID? ownerId =
                null;
            string ownerRaw =
                (request.QueryString?["owner_id"] ??
                 string.Empty)
                    .Trim();

            if (!string.IsNullOrEmpty(
                    ownerRaw))
            {
                if (!UUID.TryParse(
                        ownerRaw,
                        out UUID parsedOwner))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_owner_id",
                        "owner_id must be a UUID.");
                    return;
                }

                ownerId =
                    parsedOwner;
            }

            if (!TryPagination(
                    request,
                    response,
                    out int limit,
                    out int offset))
            {
                return;
            }

            List<EstateSettings> all =
                m_Estates.LoadEstateSettingsAll() ??
                new List<EstateSettings>();

            IEnumerable<EstateSettings> filtered =
                all.Where(x =>
                    x != null &&
                    x.EstateID > 0);

            if (!string.IsNullOrEmpty(
                    query))
            {
                filtered =
                    filtered.Where(x =>
                        (x.EstateName ?? string.Empty)
                            .IndexOf(
                                query,
                                StringComparison.OrdinalIgnoreCase) >= 0 ||
                        x.EstateID
                            .ToString()
                            .Equals(
                                query,
                                StringComparison.OrdinalIgnoreCase));
            }

            if (ownerId.HasValue)
            {
                filtered =
                    filtered.Where(x =>
                        x.EstateOwner ==
                        ownerId.Value);
            }

            EstateSettings[] ordered =
                filtered
                    .OrderBy(
                        x => x.EstateName,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        x => x.EstateID)
                    .ToArray();

            EstateSettings[] page =
                ordered
                    .Skip(offset)
                    .Take(limit)
                    .ToArray();

            string correlationId =
                NexApiRequestContext.Ensure(
                    response);

            WriteJson(
                response,
                new
                {
                    count =
                        page.Length,
                    estates =
                        page
                            .Select(EstatePayload)
                            .ToArray(),
                    pagination = new
                    {
                        limit,
                        offset,
                        returned =
                            page.Length,
                        has_more =
                            offset + page.Length <
                            ordered.Length,
                        next_offset =
                            offset + page.Length <
                            ordered.Length
                                ? offset + page.Length
                                : (int?)null
                    },
                    correlation_id =
                        correlationId
                });
        }

        private void HandleGet(
            IOSHttpResponse response,
            int estateId)
        {
            EstateSettings estate =
                LoadEstate(
                    response,
                    estateId);

            if (estate == null)
                return;

            WriteJson(
                response,
                new
                {
                    estate =
                        EstatePayload(
                            estate),
                    correlation_id =
                        Correlation(
                            response)
                });
        }

        private void HandleManagement(
            IOSHttpResponse response,
            int estateId)
        {
            EstateSettings estate =
                LoadEstate(
                    response,
                    estateId);

            if (estate == null)
                return;

            WriteJson(
                response,
                new
                {
                    estate =
                        EstateManagementPayload(
                            estate),
                    correlation_id =
                        Correlation(
                            response)
                });
        }

        private void HandleCreate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            NexPrincipal principal)
        {
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

                string name =
                    GetOptionalString(
                        root,
                        "name")
                    ?.Trim();

                if (!IsValidEstateName(
                        name))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_estate_name",
                        "name is required and may contain at most 64 characters.");
                    return;
                }

                if (!TryGetUuid(
                        root,
                        "owner_id",
                        true,
                        out UUID ownerId,
                        out string uuidError))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_owner_id",
                        uuidError);
                    return;
                }

                if (!ValidateLocalAccount(
                        response,
                        ownerId,
                        "owner_not_found",
                        "owner_id must reference a local NexVerse account."))
                {
                    return;
                }

                uint parentEstateId =
                    1;

                if (!TryGetOptionalUInt(
                        root,
                        "parent_estate_id",
                        out uint? requestedParent,
                        out string parentError))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_parent_estate_id",
                        parentError);
                    return;
                }

                if (requestedParent.HasValue)
                {
                    parentEstateId =
                        requestedParent.Value;

                    if (parentEstateId > 0 &&
                        m_Estates.LoadEstateSettings(
                            (int)parentEstateId) == null)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "parent_estate_not_found",
                            "parent_estate_id does not reference an existing Estate.");
                        return;
                    }
                }

                if (EstateNameExists(
                        name,
                        0))
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "estate_name_exists",
                        "An Estate with this name already exists.");
                    return;
                }

                if (!TryReadUuidArray(
                        root,
                        "managers",
                        (int)Constants.EstateAccessLimits.EstateManagers,
                        true,
                        response,
                        out UUID[] managers) ||
                    !TryReadUuidArray(
                        root,
                        "allowed_residents",
                        (int)Constants.EstateAccessLimits.AllowedAccess,
                        true,
                        response,
                        out UUID[] allowedResidents) ||
                    !TryReadUuidArray(
                        root,
                        "banned_residents",
                        (int)Constants.EstateAccessLimits.EstateBans,
                        true,
                        response,
                        out UUID[] bannedResidents) ||
                    !TryReadUuidArray(
                        root,
                        "allowed_groups",
                        (int)Constants.EstateAccessLimits.AllowedGroups,
                        false,
                        response,
                        out UUID[] allowedGroups))
                {
                    return;
                }

                managers =
                    managers
                        .Where(x =>
                            x != ownerId)
                        .Distinct()
                        .ToArray();

                if (!ValidateAccessLists(
                        response,
                        ownerId,
                        managers,
                        allowedResidents,
                        bannedResidents))
                {
                    return;
                }

                if (!TryReadPolicies(
                        root,
                        null,
                        response,
                        out EstatePolicyValues policies))
                {
                    return;
                }

                EstateSettings estate;

                try
                {
                    estate =
                        m_Estates.CreateNewEstate(
                            0);

                    if (estate == null ||
                        estate.EstateID == 0)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.InternalServerError,
                            "estate_create_failed",
                            "Estate storage did not create an Estate.");
                        return;
                    }

                    estate.EstateName =
                        name;
                    estate.EstateOwner =
                        ownerId;
                    estate.ParentEstateID =
                        parentEstateId;
                    estate.EstateManagers =
                        managers
                            .Where(x =>
                                x != ownerId)
                            .Distinct()
                            .ToArray();
                    estate.EstateAccess =
                        allowedResidents
                            .Distinct()
                            .ToArray();
                    estate.EstateGroups =
                        allowedGroups
                            .Distinct()
                            .ToArray();
                    estate.EstateBans =
                        BuildBans(
                            estate.EstateID,
                            bannedResidents,
                            principal);

                    ApplyPolicies(
                        estate,
                        policies);

                    m_Estates.StoreEstateSettings(
                        estate);
                }
                catch (Exception e)
                {
                    WriteError(
                        response,
                        HttpStatusCode.InternalServerError,
                        "estate_create_failed",
                        "Estate creation failed: " +
                        e.GetType().Name);
                    return;
                }

                string correlationId =
                    Correlation(
                        response);

                RecordMutation(
                    principal,
                    "estates.create",
                    estate,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["owner_id"] =
                            ownerId.ToString(),
                        ["estate_name"] =
                            name
                    });

                m_EventBus.Publish(
                    new NexEvent(
                        "estate.created",
                        "nexverse.world-api",
                        new Dictionary<string, string>
                        {
                            ["estate_id"] =
                                estate.EstateID.ToString(),
                            ["estate_name"] =
                                estate.EstateName,
                            ["owner_id"] =
                                estate.EstateOwner.ToString()
                        },
                        correlationId));

                WriteJson(
                    response,
                    new
                    {
                        estate =
                            EstateManagementPayload(
                                estate),
                        correlation_id =
                            correlationId
                    },
                    HttpStatusCode.Created);
            }
        }


        private void HandleDelete(IOSHttpResponse response, int estateId, NexPrincipal principal)
        {
            EstateSettings estate = LoadEstate(response, estateId);
            if (estate == null) return;
            UUID[] regions = SafeRegions(estate);
            if (regions.Length != 0)
            {
                WriteError(response, HttpStatusCode.Conflict, "estate_not_empty",
                    "Estate deletion is refused while regions are assigned. Reassign all regions first.");
                return;
            }
            if (!m_Estates.DeleteEstate(estateId))
            {
                WriteError(response, HttpStatusCode.InternalServerError, "estate_delete_failed",
                    "The authoritative Estate datastore refused the delete operation.");
                return;
            }
            string correlationId = NexApiRequestContext.Ensure(response);
            m_Audit.Record(new NexAuditEvent(
                principal?.Subject ?? "unknown",
                "estate.delete",
                "estate:" + estateId,
                correlationId,
                new Dictionary<string,string> { ["estate_name"] = estate.EstateName ?? string.Empty }));
            m_EventBus.Publish(new NexEvent("estate.deleted", "nexverse.robust",
                new Dictionary<string,string> { ["estate_id"] = estateId.ToString(), ["correlation_id"] = correlationId }));
            WriteJson(response, new { deleted = true, estate_id = estateId, correlation_id = correlationId });
        }

        private void HandleUpdate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            int estateId,
            NexPrincipal principal)
        {
            EstateSettings estate =
                LoadEstate(
                    response,
                    estateId);

            if (estate == null)
                return;

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
                List<string> changed =
                    new List<string>();

                string name =
                    estate.EstateName;
                UUID ownerId =
                    estate.EstateOwner;
                uint parentEstateId =
                    estate.ParentEstateID;
                UUID[] managers =
                    estate.EstateManagers ??
                    Array.Empty<UUID>();
                UUID[] allowedResidents =
                    estate.EstateAccess ??
                    Array.Empty<UUID>();
                UUID[] bannedResidents =
                    (estate.EstateBans ??
                     Array.Empty<EstateBan>())
                    .Select(x =>
                        x.BannedUserID)
                    .ToArray();
                UUID[] allowedGroups =
                    estate.EstateGroups ??
                    Array.Empty<UUID>();

                if (root.TryGetProperty(
                        "name",
                        out JsonElement _))
                {
                    name =
                        GetOptionalString(
                            root,
                            "name")
                        ?.Trim();

                    if (!IsValidEstateName(
                            name))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_estate_name",
                            "name must contain between 1 and 64 characters.");
                        return;
                    }

                    if (EstateNameExists(
                            name,
                            estateId))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.Conflict,
                            "estate_name_exists",
                            "Another Estate already uses this name.");
                        return;
                    }

                    changed.Add(
                        "name");
                }

                if (root.TryGetProperty(
                        "owner_id",
                        out JsonElement _))
                {
                    if (!TryGetUuid(
                            root,
                            "owner_id",
                            true,
                            out ownerId,
                            out string uuidError))
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_owner_id",
                            uuidError);
                        return;
                    }

                    if (!ValidateLocalAccount(
                            response,
                            ownerId,
                            "owner_not_found",
                            "owner_id must reference a local NexVerse account."))
                    {
                        return;
                    }

                    changed.Add(
                        "owner_id");
                }

                if (root.TryGetProperty(
                        "parent_estate_id",
                        out JsonElement _))
                {
                    if (!TryGetOptionalUInt(
                            root,
                            "parent_estate_id",
                            out uint? parent,
                            out string parentError) ||
                        !parent.HasValue)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_parent_estate_id",
                            parentError);
                        return;
                    }

                    parentEstateId =
                        parent.Value;

                    if (parentEstateId ==
                        estate.EstateID)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "invalid_parent_estate_id",
                            "An Estate cannot be its own parent.");
                        return;
                    }

                    if (parentEstateId > 0 &&
                        m_Estates.LoadEstateSettings(
                            (int)parentEstateId) == null)
                    {
                        WriteError(
                            response,
                            HttpStatusCode.BadRequest,
                            "parent_estate_not_found",
                            "parent_estate_id does not reference an existing Estate.");
                        return;
                    }

                    changed.Add(
                        "parent_estate_id");
                }

                if (root.TryGetProperty(
                        "managers",
                        out JsonElement _))
                {
                    if (!TryReadUuidArray(
                            root,
                            "managers",
                            (int)Constants.EstateAccessLimits.EstateManagers,
                            true,
                            response,
                            out managers))
                    {
                        return;
                    }

                    managers =
                        managers
                            .Where(x =>
                                x != ownerId)
                            .Distinct()
                            .ToArray();
                    changed.Add(
                        "managers");
                }

                if (root.TryGetProperty(
                        "allowed_residents",
                        out JsonElement _))
                {
                    if (!TryReadUuidArray(
                            root,
                            "allowed_residents",
                            (int)Constants.EstateAccessLimits.AllowedAccess,
                            true,
                            response,
                            out allowedResidents))
                    {
                        return;
                    }

                    changed.Add(
                        "allowed_residents");
                }

                if (root.TryGetProperty(
                        "banned_residents",
                        out JsonElement _))
                {
                    if (!TryReadUuidArray(
                            root,
                            "banned_residents",
                            (int)Constants.EstateAccessLimits.EstateBans,
                            true,
                            response,
                            out bannedResidents))
                    {
                        return;
                    }

                    changed.Add(
                        "banned_residents");
                }

                if (root.TryGetProperty(
                        "allowed_groups",
                        out JsonElement _))
                {
                    if (!TryReadUuidArray(
                            root,
                            "allowed_groups",
                            (int)Constants.EstateAccessLimits.AllowedGroups,
                            false,
                            response,
                            out allowedGroups))
                    {
                        return;
                    }

                    changed.Add(
                        "allowed_groups");
                }

                if (!ValidateAccessLists(
                        response,
                        ownerId,
                        managers
                            .Where(x =>
                                x != ownerId)
                            .Distinct()
                            .ToArray(),
                        allowedResidents,
                        bannedResidents))
                {
                    return;
                }

                if (!TryReadPolicies(
                        root,
                        estate,
                        response,
                        out EstatePolicyValues policies,
                        changed))
                {
                    return;
                }

                if (changed.Count == 0)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "no_changes",
                        "At least one supported Estate field must be supplied.");
                    return;
                }

                try
                {
                    estate.EstateName =
                        name;
                    estate.EstateOwner =
                        ownerId;
                    estate.ParentEstateID =
                        parentEstateId;
                    estate.EstateManagers =
                        managers
                            .Where(x =>
                                x != ownerId)
                            .Distinct()
                            .ToArray();
                    estate.EstateAccess =
                        allowedResidents
                            .Distinct()
                            .ToArray();
                    estate.EstateGroups =
                        allowedGroups
                            .Distinct()
                            .ToArray();

                    if (changed.Contains(
                            "banned_residents"))
                    {
                        estate.EstateBans =
                            BuildBans(
                                estate.EstateID,
                                bannedResidents,
                                principal);
                    }

                    ApplyPolicies(
                        estate,
                        policies);

                    m_Estates.StoreEstateSettings(
                        estate);
                }
                catch (Exception e)
                {
                    WriteError(
                        response,
                        HttpStatusCode.InternalServerError,
                        "estate_update_failed",
                        "Estate update failed: " +
                        e.GetType().Name);
                    return;
                }

                string correlationId =
                    Correlation(
                        response);

                RecordMutation(
                    principal,
                    "estates.update",
                    estate,
                    correlationId,
                    new Dictionary<string, string>
                    {
                        ["changed_fields"] =
                            string.Join(
                                ",",
                                changed.Distinct())
                    });

                m_EventBus.Publish(
                    new NexEvent(
                        "estate.updated",
                        "nexverse.world-api",
                        new Dictionary<string, string>
                        {
                            ["estate_id"] =
                                estate.EstateID.ToString(),
                            ["changed_fields"] =
                                string.Join(
                                    ",",
                                    changed.Distinct())
                        },
                        correlationId));

                WriteJson(
                    response,
                    new
                    {
                        estate =
                            EstateManagementPayload(
                                estate),
                        changed_fields =
                            changed
                                .Distinct()
                                .ToArray(),
                        correlation_id =
                            correlationId
                    });
            }
        }

        private void HandleRegionAssignment(
            IOSHttpResponse response,
            int estateId,
            UUID regionId,
            NexPrincipal principal)
        {
            EstateSettings estate =
                LoadEstate(
                    response,
                    estateId);

            if (estate == null)
                return;

            if (m_Grid == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "grid_service_unavailable",
                    "GridService is required for Estate region assignment.");
                return;
            }

            GridRegion region =
                m_Grid.GetRegionByUUID(
                    UUID.Zero,
                    regionId);

            if (region == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "region_not_found",
                    "The grid region was not found.");
                return;
            }

            EstateSettings currentEstate =
                m_Estates.LoadEstateSettings(
                    regionId,
                    false);

            if (currentEstate != null &&
                currentEstate.EstateID ==
                estate.EstateID)
            {
                WriteJson(
                    response,
                    new
                    {
                        estate =
                            EstatePayload(
                                estate),
                        region_id =
                            regionId.ToString(),
                        already_linked =
                            true,
                        region_restart_required =
                            false,
                        correlation_id =
                            Correlation(
                                response)
                    });
                return;
            }

            if (!m_Estates.LinkRegion(
                    regionId,
                    estateId))
            {
                WriteError(
                    response,
                    HttpStatusCode.InternalServerError,
                    "estate_region_link_failed",
                    "The region could not be assigned to the Estate.");
                return;
            }

            string correlationId =
                Correlation(
                    response);

            Dictionary<string, string> details =
                new Dictionary<string, string>
                {
                    ["region_id"] =
                        regionId.ToString(),
                    ["region_name"] =
                        region.RegionName ?? string.Empty
                };

            if (currentEstate != null &&
                currentEstate.EstateID > 0)
            {
                details["previous_estate_id"] =
                    currentEstate.EstateID.ToString();
            }

            RecordMutation(
                principal,
                "estates.region.assign",
                estate,
                correlationId,
                details);

            m_EventBus.Publish(
                new NexEvent(
                    "estate.region.assigned",
                    "nexverse.world-api",
                    new Dictionary<string, string>
                    {
                        ["estate_id"] =
                            estate.EstateID.ToString(),
                        ["region_id"] =
                            regionId.ToString(),
                        ["previous_estate_id"] =
                            currentEstate?.EstateID
                                .ToString() ??
                            string.Empty
                    },
                    correlationId));

            WriteJson(
                response,
                new
                {
                    estate =
                        EstatePayload(
                            estate),
                    region_id =
                        regionId.ToString(),
                    previous_estate_id =
                        currentEstate?.EstateID,
                    already_linked =
                        false,
                    region_restart_required =
                        true,
                    message =
                        "Estate assignment is persisted. Restart the region so its live EstateSettings are reloaded.",
                    correlation_id =
                        correlationId
                });
        }

        private EstateSettings LoadEstate(
            IOSHttpResponse response,
            int estateId)
        {
            EstateSettings estate =
                m_Estates.LoadEstateSettings(
                    estateId);

            if (estate == null ||
                estate.EstateID == 0)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "estate_not_found",
                    "Estate was not found.");
                return null;
            }

            return estate;
        }

        private object EstatePayload(
            EstateSettings estate)
        {
            UUID[] regions =
                SafeRegions(
                    estate);

            return new
            {
                estate_id =
                    estate.EstateID,
                name =
                    estate.EstateName ??
                    string.Empty,
                owner_id =
                    estate.EstateOwner
                        .ToString(),
                parent_estate_id =
                    estate.ParentEstateID,
                region_count =
                    regions.Length
            };
        }

        private object EstateManagementPayload(
            EstateSettings estate)
        {
            UUID[] regions =
                SafeRegions(
                    estate);

            return new
            {
                estate_id =
                    estate.EstateID,
                name =
                    estate.EstateName ??
                    string.Empty,
                owner_id =
                    estate.EstateOwner
                        .ToString(),
                parent_estate_id =
                    estate.ParentEstateID,
                managers =
                    (estate.EstateManagers ??
                     Array.Empty<UUID>())
                    .Select(x =>
                        x.ToString())
                    .ToArray(),
                allowed_residents =
                    (estate.EstateAccess ??
                     Array.Empty<UUID>())
                    .Select(x =>
                        x.ToString())
                    .ToArray(),
                banned_residents =
                    (estate.EstateBans ??
                     Array.Empty<EstateBan>())
                    .Select(x =>
                        x.BannedUserID
                            .ToString())
                    .ToArray(),
                allowed_groups =
                    (estate.EstateGroups ??
                     Array.Empty<UUID>())
                    .Select(x =>
                        x.ToString())
                    .ToArray(),
                policies = new
                {
                    public_access =
                        estate.PublicAccess,
                    allow_voice =
                        estate.AllowVoice,
                    allow_direct_teleport =
                        estate.AllowDirectTeleport,
                    estate_skip_scripts =
                        estate.EstateSkipScripts,
                    deny_anonymous =
                        estate.DenyAnonymous,
                    deny_minors =
                        estate.DenyMinors,
                    allow_environment_override =
                        estate.AllowEnvironmentOverride
                },
                region_count =
                    regions.Length,
                region_ids =
                    regions
                        .Select(x =>
                            x.ToString())
                        .ToArray()
            };
        }

        private UUID[] SafeRegions(
            EstateSettings estate)
        {
            try
            {
                return
                    m_Estates.GetRegions(
                        (int)estate.EstateID)
                    ?.Where(x =>
                        !x.IsZero())
                    .Distinct()
                    .ToArray() ??
                    Array.Empty<UUID>();
            }
            catch
            {
                return Array.Empty<UUID>();
            }
        }

        private bool EstateNameExists(
            string name,
            int excludedEstateId)
        {
            return
                (m_Estates.LoadEstateSettingsAll() ??
                 new List<EstateSettings>())
                .Any(x =>
                    x != null &&
                    x.EstateID !=
                        excludedEstateId &&
                    string.Equals(
                        (x.EstateName ??
                         string.Empty)
                            .Trim(),
                        name,
                        StringComparison.OrdinalIgnoreCase));
        }

        private bool ValidateLocalAccount(
            IOSHttpResponse response,
            UUID userId,
            string error,
            string message)
        {
            if (m_UserAccounts == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "user_account_service_unavailable",
                    "UserAccountService is required for Estate owner and resident-list validation.");
                return false;
            }

            UserAccount account =
                m_UserAccounts.GetUserAccount(
                    UUID.Zero,
                    userId);

            if (account == null ||
                !account.LocalToGrid)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    error,
                    message);
                return false;
            }

            return true;
        }

        private bool TryReadUuidArray(
            JsonElement root,
            string propertyName,
            int maximum,
            bool requireLocalAccounts,
            IOSHttpResponse response,
            out UUID[] values)
        {
            values =
                Array.Empty<UUID>();

            if (!root.TryGetProperty(
                    propertyName,
                    out JsonElement element))
            {
                return true;
            }

            if (element.ValueKind !=
                JsonValueKind.Array)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_" + propertyName,
                    propertyName +
                    " must be an array of UUID strings.");
                return false;
            }

            List<UUID> parsed =
                new List<UUID>();

            foreach (JsonElement item in
                     element.EnumerateArray())
            {
                if (item.ValueKind !=
                        JsonValueKind.String ||
                    !UUID.TryParse(
                        item.GetString(),
                        out UUID id) ||
                    id.IsZero())
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_" + propertyName,
                        propertyName +
                        " contains an invalid UUID.");
                    return false;
                }

                if (!parsed.Contains(
                        id))
                {
                    parsed.Add(
                        id);
                }
            }

            if (parsed.Count >
                maximum)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "too_many_" + propertyName,
                    propertyName +
                    " exceeds the OpenSim Estate limit of " +
                    maximum +
                    ".");
                return false;
            }

            if (requireLocalAccounts)
            {
                foreach (UUID id in
                         parsed)
                {
                    if (!ValidateLocalAccount(
                            response,
                            id,
                            propertyName +
                            "_account_not_found",
                            propertyName +
                            " contains a UUID that is not a local NexVerse account."))
                    {
                        return false;
                    }
                }
            }

            values =
                parsed.ToArray();
            return true;
        }

        private static bool ValidateAccessLists(
            IOSHttpResponse response,
            UUID ownerId,
            IEnumerable<UUID> managers,
            IEnumerable<UUID> allowedResidents,
            IEnumerable<UUID> bannedResidents)
        {
            HashSet<UUID> managerSet =
                new HashSet<UUID>(
                    managers ??
                    Enumerable.Empty<UUID>());
            HashSet<UUID> allowedSet =
                new HashSet<UUID>(
                    allowedResidents ??
                    Enumerable.Empty<UUID>());

            foreach (UUID banned in
                     bannedResidents ??
                     Enumerable.Empty<UUID>())
            {
                if (banned ==
                        ownerId ||
                    managerSet.Contains(
                        banned))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_banned_residents",
                        "Owner and Estate managers cannot be present in banned_residents.");
                    return false;
                }

                if (allowedSet.Contains(
                        banned))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "conflicting_estate_access",
                        "The same resident cannot be both allowed and banned.");
                    return false;
                }
            }

            return true;
        }

        private static EstateBan[] BuildBans(
            uint estateId,
            IEnumerable<UUID> users,
            NexPrincipal principal)
        {
            UUID banningUser =
                UUID.Zero;

            if (UUID.TryParse(
                    principal?.Subject,
                    out UUID parsedActor))
            {
                banningUser =
                    parsedActor;
            }

            int now =
                Util.UnixTimeSinceEpoch();

            return
                (users ??
                 Enumerable.Empty<UUID>())
                .Distinct()
                .Select(x =>
                    new EstateBan
                    {
                        EstateID =
                            estateId,
                        BannedUserID =
                            x,
                        BanningUserID =
                            banningUser,
                        BanTime =
                            now
                    })
                .ToArray();
        }

        private bool TryReadPolicies(
            JsonElement root,
            EstateSettings current,
            IOSHttpResponse response,
            out EstatePolicyValues values,
            List<string> changed = null)
        {
            values =
                new EstatePolicyValues
                {
                    PublicAccess =
                        current?.PublicAccess ??
                        true,
                    AllowVoice =
                        current?.AllowVoice ??
                        true,
                    AllowDirectTeleport =
                        current?.AllowDirectTeleport ??
                        true,
                    EstateSkipScripts =
                        current?.EstateSkipScripts ??
                        false,
                    DenyAnonymous =
                        current?.DenyAnonymous ??
                        false,
                    DenyMinors =
                        current?.DenyMinors ??
                        false,
                    AllowEnvironmentOverride =
                        current?.AllowEnvironmentOverride ??
                        false
                };

            foreach (string property in
                     new[]
                     {
                         "public_access",
                         "allow_voice",
                         "allow_direct_teleport",
                         "estate_skip_scripts",
                         "deny_anonymous",
                         "deny_minors",
                         "allow_environment_override"
                     })
            {
                if (!root.TryGetProperty(
                        property,
                        out JsonElement element))
                {
                    continue;
                }

                if (element.ValueKind !=
                        JsonValueKind.True &&
                    element.ValueKind !=
                        JsonValueKind.False)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_" + property,
                        property +
                        " must be a boolean.");
                    return false;
                }

                bool value =
                    element.GetBoolean();

                switch (property)
                {
                    case "public_access":
                        values.PublicAccess =
                            value;
                        break;
                    case "allow_voice":
                        values.AllowVoice =
                            value;
                        break;
                    case "allow_direct_teleport":
                        values.AllowDirectTeleport =
                            value;
                        break;
                    case "estate_skip_scripts":
                        values.EstateSkipScripts =
                            value;
                        break;
                    case "deny_anonymous":
                        values.DenyAnonymous =
                            value;
                        break;
                    case "deny_minors":
                        values.DenyMinors =
                            value;
                        break;
                    case "allow_environment_override":
                        values.AllowEnvironmentOverride =
                            value;
                        break;
                }

                changed?.Add(
                    property);
            }

            return true;
        }

        private static void ApplyPolicies(
            EstateSettings estate,
            EstatePolicyValues policies)
        {
            estate.PublicAccess =
                policies.PublicAccess;
            estate.AllowVoice =
                policies.AllowVoice;
            estate.AllowDirectTeleport =
                policies.AllowDirectTeleport;
            estate.EstateSkipScripts =
                policies.EstateSkipScripts;
            estate.DenyAnonymous =
                policies.DenyAnonymous;
            estate.DenyMinors =
                policies.DenyMinors;
            estate.AllowEnvironmentOverride =
                policies.AllowEnvironmentOverride;
        }

        private static bool TryGetUuid(
            JsonElement root,
            string propertyName,
            bool required,
            out UUID value,
            out string error)
        {
            value =
                UUID.Zero;
            error =
                propertyName +
                " must be a non-zero UUID.";

            if (!root.TryGetProperty(
                    propertyName,
                    out JsonElement element))
            {
                return !required;
            }

            return
                element.ValueKind ==
                    JsonValueKind.String &&
                UUID.TryParse(
                    element.GetString(),
                    out value) &&
                !value.IsZero();
        }

        private static bool TryGetOptionalUInt(
            JsonElement root,
            string propertyName,
            out uint? value,
            out string error)
        {
            value =
                null;
            error =
                propertyName +
                " must be a non-negative integer.";

            if (!root.TryGetProperty(
                    propertyName,
                    out JsonElement element))
            {
                return true;
            }

            if (element.ValueKind !=
                    JsonValueKind.Number ||
                !element.TryGetUInt32(
                    out uint parsed))
            {
                return false;
            }

            value =
                parsed;
            return true;
        }

        private static bool IsValidEstateName(
            string name)
        {
            return
                !string.IsNullOrWhiteSpace(
                    name) &&
                name.Trim().Length <=
                    64 &&
                name.IndexOfAny(
                    new[]
                    {
                        '\r',
                        '\n'
                    }) < 0;
        }

        private void RecordMutation(
            NexPrincipal principal,
            string action,
            EstateSettings estate,
            string correlationId,
            IDictionary<string, string> details)
        {
            Dictionary<string, string> auditDetails =
                new Dictionary<string, string>(
                    details ??
                    new Dictionary<string, string>())
                {
                    ["estate_name"] =
                        estate.EstateName ??
                        string.Empty,
                    ["owner_id"] =
                        estate.EstateOwner
                            .ToString()
                };

            m_Audit.Record(
                new NexAuditEvent(
                    principal?.Subject ??
                        "unknown",
                    action,
                    "estate:" +
                    estate.EstateID,
                    correlationId,
                    auditDetails));
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string scope,
            out NexPrincipal principal)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    scope,
                    out principal,
                    out UserAccount _,
                    out int statusCode,
                    out string error))
            {
                return true;
            }

            response.AddHeader(
                "WWW-Authenticate",
                "Bearer");

            WriteError(
                response,
                (HttpStatusCode)statusCode,
                error,
                "Authentication or " +
                scope +
                " authorization is required.");

            return false;
        }

        private static bool TryPagination(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out int limit,
            out int offset)
        {
            limit =
                50;
            offset =
                0;

            string limitRaw =
                request.QueryString?["limit"];
            string offsetRaw =
                request.QueryString?["offset"];

            if (!string.IsNullOrWhiteSpace(
                    limitRaw) &&
                (!int.TryParse(
                    limitRaw,
                    out limit) ||
                 limit < 1 ||
                 limit > 100))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_pagination",
                    "limit must be between 1 and 100.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(
                    offsetRaw) &&
                (!int.TryParse(
                    offsetRaw,
                    out offset) ||
                 offset < 0 ||
                 offset > 10000))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_pagination",
                    "offset must be between 0 and 10000.");
                return false;
            }

            return true;
        }

        private static bool TryReadJson(
            IOSHttpRequest request,
            IOSHttpResponse response,
            out JsonDocument document)
        {
            document =
                null;

            try
            {
                using StreamReader reader =
                    new StreamReader(
                        request.InputStream,
                        Encoding.UTF8,
                        true,
                        1024,
                        true);

                string body =
                    reader.ReadToEnd();

                if (string.IsNullOrWhiteSpace(
                        body))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_json",
                        "Request body must contain a JSON object.");
                    return false;
                }

                document =
                    JsonDocument.Parse(
                        body);

                if (document.RootElement.ValueKind !=
                    JsonValueKind.Object)
                {
                    document.Dispose();
                    document =
                        null;
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_json",
                        "Request body must contain a JSON object.");
                    return false;
                }

                return true;
            }
            catch
            {
                document?.Dispose();
                document =
                    null;
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_json",
                    "Request body must contain valid JSON.");
                return false;
            }
        }

        private static string GetOptionalString(
            JsonElement root,
            string name)
        {
            if (!root.TryGetProperty(
                    name,
                    out JsonElement element) ||
                element.ValueKind !=
                    JsonValueKind.String)
            {
                return null;
            }

            return element.GetString();
        }

        private static bool IsMethod(
            IOSHttpRequest request,
            string method)
        {
            return
                request != null &&
                string.Equals(
                    request.HttpMethod,
                    method,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteMethodError(
            IOSHttpResponse response,
            string message)
        {
            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                message);
        }

        private static string Correlation(
            IOSHttpResponse response)
        {
            return
                NexApiRequestContext.Ensure(
                    response);
        }

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string message)
        {
            string correlationId =
                Correlation(
                    response);

            WriteJson(
                response,
                new
                {
                    error,
                    message,
                    correlation_id =
                        correlationId
                },
                status);
        }

        private static void WriteJson(
            IOSHttpResponse response,
            object payload,
            HttpStatusCode status =
                HttpStatusCode.OK)
        {
            response.KeepAlive =
                false;
            response.StatusCode =
                (int)status;
            response.ContentType =
                "application/json; charset=utf-8";
            response.RawBuffer =
                JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    s_Json);
        }

        private sealed class EstatePolicyValues
        {
            public bool PublicAccess { get; set; }
            public bool AllowVoice { get; set; }
            public bool AllowDirectTeleport { get; set; }
            public bool EstateSkipScripts { get; set; }
            public bool DenyAnonymous { get; set; }
            public bool DenyMinors { get; set; }
            public bool AllowEnvironmentOverride { get; set; }
        }
    }
}
