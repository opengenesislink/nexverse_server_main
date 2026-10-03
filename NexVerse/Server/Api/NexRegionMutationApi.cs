// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexVerse.Core.Audit;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Authenticated asynchronous mutation surface for NexVerse-managed regions.
    /// Robust validates intent and placement; a targeted simulator NodeAgent
    /// performs the actual scene lifecycle operation.
    /// </summary>
    internal sealed class NexRegionMutationApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly IGridService m_Grid;
        private readonly NexNodeRegistry m_Nodes;
        private readonly NexRegionOperationRegistry m_Operations;
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly INexEventBus m_EventBus;
        private readonly INexAuditSink m_Audit;
        private readonly INexIdempotencyStore m_Idempotency;
        private readonly int m_IdempotencyTtlSeconds;
        private readonly bool m_DistributedTransportEnabled;

        public NexRegionMutationApi(
            IGridService grid,
            NexNodeRegistry nodes,
            NexRegionOperationRegistry operations,
            NexApiAuthenticator authenticator,
            INexEventBus eventBus,
            INexAuditSink audit,
            INexIdempotencyStore idempotency,
            int idempotencyTtlSeconds,
            bool distributedTransportEnabled)
        {
            m_Grid = grid;
            m_Nodes =
                nodes ??
                throw new ArgumentNullException(nameof(nodes));
            m_Operations =
                operations ??
                throw new ArgumentNullException(nameof(operations));
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
            m_EventBus =
                eventBus ??
                throw new ArgumentNullException(nameof(eventBus));
            m_Audit =
                audit ??
                NullNexAuditSink.Instance;
            m_Idempotency =
                idempotency;
            m_IdempotencyTtlSeconds =
                Math.Max(
                    60,
                    idempotencyTtlSeconds);
            m_DistributedTransportEnabled =
                distributedTransportEnabled;
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');

            if (string.Equals(
                    path,
                    "/api/v1/regions",
                    StringComparison.OrdinalIgnoreCase))
            {
                HandleCreate(
                    request,
                    response);
                return;
            }

            const string regionPrefix =
                "/api/v1/regions/";
            const string placementSuffix =
                "/placement";

            if (path.StartsWith(
                    regionPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(
                    placementSuffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string regionId =
                    path.Substring(
                        regionPrefix.Length,
                        path.Length -
                        regionPrefix.Length -
                        placementSuffix.Length)
                        .Trim('/');

                HandleMove(
                    request,
                    response,
                    regionId);
                return;
            }

            const string operationPrefix =
                "/api/v1/region-operations/";

            if (path.StartsWith(
                    operationPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string operationId =
                    Uri.UnescapeDataString(
                        path.Substring(
                            operationPrefix.Length));

                HandleOperation(
                    request,
                    response,
                    operationId);
                return;
            }

            WriteError(
                response,
                HttpStatusCode.NotFound,
                "not_found",
                "Unknown NexVerse region-mutation endpoint.");
        }

        private void HandleCreate(
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

            if (!Authenticate(
                    request,
                    response,
                    NexScopes.RegionsManage,
                    out NexPrincipal principal))
            {
                return;
            }

            if (!RequireMutationInfrastructure(
                    response))
            {
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

                string name =
                    GetOptionalString(
                        root,
                        "name")
                    ?.Trim();

                string nodeId =
                    GetOptionalString(
                        root,
                        "node_id")
                    ?.Trim();

                string regionIdRaw =
                    GetOptionalString(
                        root,
                        "region_id")
                    ?.Trim();

                if (!TryGetRequiredInt(
                        root,
                        "estate_id",
                        out int estateId) ||
                    !TryGetRequiredInt(
                        root,
                        "grid_x",
                        out int gridX) ||
                    !TryGetRequiredInt(
                        root,
                        "grid_y",
                        out int gridY))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_request",
                        "estate_id, grid_x and grid_y are required integer fields.");
                    return;
                }

                int sizeX =
                    GetOptionalInt(
                        root,
                        "size_x",
                        (int)Constants.RegionSize);
                int sizeY =
                    GetOptionalInt(
                        root,
                        "size_y",
                        (int)Constants.RegionSize);

                if (!IsSafeRegionName(name))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_region_name",
                        "name is required, may contain at most 128 characters and may not contain line breaks or INI section delimiters.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(nodeId) ||
                    nodeId.Length > 128)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_node_id",
                        "node_id is required and may contain at most 128 characters.");
                    return;
                }

                if (estateId <= 0)
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_estate_id",
                        "estate_id must be a positive integer.");
                    return;
                }

                if (!TryValidateGeometry(
                        response,
                        gridX,
                        gridY,
                        sizeX,
                        sizeY))
                {
                    return;
                }

                UUID regionId;

                if (string.IsNullOrWhiteSpace(
                        regionIdRaw))
                {
                    regionId =
                        UUID.Random();
                }
                else if (!UUID.TryParse(
                             regionIdRaw,
                             out regionId) ||
                         regionId.IsZero())
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_region_id",
                        "region_id must be a non-zero UUID when supplied.");
                    return;
                }

                NexNodeSnapshot node =
                    m_Nodes.Get(nodeId);

                if (!ValidateTargetNode(
                        response,
                        node))
                {
                    return;
                }

                if (m_Grid.GetRegionByUUID(
                        UUID.Zero,
                        regionId) != null)
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "region_id_exists",
                        "A grid region with this UUID already exists.");
                    return;
                }

                if (m_Grid.GetLocalRegionByName(
                        UUID.Zero,
                        name) != null)
                {
                    WriteError(
                        response,
                        HttpStatusCode.Conflict,
                        "region_name_exists",
                        "A local grid region with this name already exists.");
                    return;
                }

                if (!TryValidatePlacement(
                        gridX,
                        gridY,
                        sizeX,
                        sizeY,
                        UUID.Zero,
                        out string placementReason,
                        out GridRegion[] conflicts))
                {
                    WritePlacementConflict(
                        response,
                        placementReason,
                        conflicts);
                    return;
                }

                if (!TryReserveIdempotency(
                        request,
                        response,
                        principal.Subject,
                        "POST",
                        "/api/v1/regions",
                        root,
                        out IdempotencyReservation reservation))
                {
                    return;
                }

                bool completed =
                    false;

                try
                {
                    string correlationId =
                        Correlation(response);
                    string operationId =
                        Guid.NewGuid()
                            .ToString("N");

                    Dictionary<string, string> requested =
                        new Dictionary<string, string>
                        {
                            ["region_name"] = name,
                            ["grid_x"] = gridX.ToString(),
                            ["grid_y"] = gridY.ToString(),
                            ["size_x"] = sizeX.ToString(),
                            ["size_y"] = sizeY.ToString(),
                            ["estate_id"] = estateId.ToString()
                        };

                    NexRegionOperationSnapshot operation =
                        m_Operations.Register(
                            operationId,
                            "create",
                            node.NodeId,
                            regionId.ToString(),
                            principal.Subject,
                            correlationId,
                            requested);

                    m_Audit.Record(
                        new NexAuditEvent(
                            principal.Subject,
                            "regions.create.request",
                            regionId.ToString(),
                            correlationId,
                            new Dictionary<string, string>
                            {
                                ["operation_id"] =
                                    operationId,
                                ["region_name"] =
                                    name,
                                ["node_id"] =
                                    node.NodeId,
                                ["estate_id"] =
                                    estateId.ToString(),
                                ["grid_x"] =
                                    gridX.ToString(),
                                ["grid_y"] =
                                    gridY.ToString(),
                                ["size_x"] =
                                    sizeX.ToString(),
                                ["size_y"] =
                                    sizeY.ToString()
                            }));

                    m_EventBus.Publish(
                        new NexEvent(
                            "region.control.create.requested",
                            "nexverse.world-api",
                            new Dictionary<string, string>
                            {
                                ["operation_id"] =
                                    operationId,
                                ["target_node_id"] =
                                    node.NodeId,
                                ["region_id"] =
                                    regionId.ToString(),
                                ["region_name"] =
                                    name,
                                ["estate_id"] =
                                    estateId.ToString(),
                                ["grid_x"] =
                                    gridX.ToString(),
                                ["grid_y"] =
                                    gridY.ToString(),
                                ["size_x"] =
                                    sizeX.ToString(),
                                ["size_y"] =
                                    sizeY.ToString()
                            },
                            correlationId));

                    WriteJson(
                        response,
                        new
                        {
                            operation =
                                OperationPayload(
                                    operation),
                            requested_region = new
                            {
                                region_id =
                                    regionId.ToString(),
                                name,
                                node_id =
                                    node.NodeId,
                                estate_id =
                                    estateId,
                                grid_x =
                                    gridX,
                                grid_y =
                                    gridY,
                                size_x =
                                    sizeX,
                                size_y =
                                    sizeY
                            },
                            correlation_id =
                                correlationId
                        },
                        HttpStatusCode.Accepted);

                    CompleteIdempotency(
                        response,
                        reservation);
                    completed = true;
                }
                finally
                {
                    if (!completed)
                        AbortIdempotency(
                            reservation);
                }
            }
        }

        private void HandleMove(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string regionIdRaw)
        {
            if (!RequireMethod(
                    request,
                    response,
                    "PATCH"))
            {
                return;
            }

            if (!Authenticate(
                    request,
                    response,
                    NexScopes.RegionsManage,
                    out NexPrincipal principal))
            {
                return;
            }

            if (!RequireMutationInfrastructure(
                    response))
            {
                return;
            }

            if (!UUID.TryParse(
                    regionIdRaw,
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

            GridRegion existing =
                m_Grid.GetRegionByUUID(
                    UUID.Zero,
                    regionId);

            if (existing == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "region_not_found",
                    "The grid region was not found.");
                return;
            }

            NexNodeSnapshot node =
                m_Nodes.FindNodeForRegion(
                    regionId.ToString());

            if (!ValidateTargetNode(
                    response,
                    node))
            {
                return;
            }

            NexNodeRegionSnapshot nodeRegion =
                node.Regions.FirstOrDefault(x =>
                    string.Equals(
                        x.RegionId,
                        regionId.ToString(),
                        StringComparison.OrdinalIgnoreCase));

            if (nodeRegion != null &&
                nodeRegion.AgentCount > 0)
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "region_has_agents",
                    "A running region cannot be moved while root agents are present.");
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

                if (!TryGetRequiredInt(
                        root,
                        "grid_x",
                        out int gridX) ||
                    !TryGetRequiredInt(
                        root,
                        "grid_y",
                        out int gridY))
                {
                    WriteError(
                        response,
                        HttpStatusCode.BadRequest,
                        "invalid_request",
                        "grid_x and grid_y are required integer fields.");
                    return;
                }

                int sizeX =
                    NormalizeRegionSize(
                        existing.RegionSizeX);
                int sizeY =
                    NormalizeRegionSize(
                        existing.RegionSizeY);

                if (!TryValidateGeometry(
                        response,
                        gridX,
                        gridY,
                        sizeX,
                        sizeY))
                {
                    return;
                }

                if (!TryValidatePlacement(
                        gridX,
                        gridY,
                        sizeX,
                        sizeY,
                        regionId,
                        out string placementReason,
                        out GridRegion[] conflicts))
                {
                    WritePlacementConflict(
                        response,
                        placementReason,
                        conflicts);
                    return;
                }

                string path =
                    "/api/v1/regions/" +
                    regionId +
                    "/placement";

                if (!TryReserveIdempotency(
                        request,
                        response,
                        principal.Subject,
                        "PATCH",
                        path,
                        root,
                        out IdempotencyReservation reservation))
                {
                    return;
                }

                bool completed =
                    false;

                try
                {
                    string correlationId =
                        Correlation(response);
                    string operationId =
                        Guid.NewGuid()
                            .ToString("N");

                    int oldGridX =
                        FloorDiv(
                            existing.RegionLocX,
                            (int)Constants.RegionSize);
                    int oldGridY =
                        FloorDiv(
                            existing.RegionLocY,
                            (int)Constants.RegionSize);

                    NexRegionOperationSnapshot operation =
                        m_Operations.Register(
                            operationId,
                            "move",
                            node.NodeId,
                            regionId.ToString(),
                            principal.Subject,
                            correlationId,
                            new Dictionary<string, string>
                            {
                                ["old_grid_x"] =
                                    oldGridX.ToString(),
                                ["old_grid_y"] =
                                    oldGridY.ToString(),
                                ["grid_x"] =
                                    gridX.ToString(),
                                ["grid_y"] =
                                    gridY.ToString()
                            });

                    m_Audit.Record(
                        new NexAuditEvent(
                            principal.Subject,
                            "regions.placement.update.request",
                            regionId.ToString(),
                            correlationId,
                            new Dictionary<string, string>
                            {
                                ["operation_id"] =
                                    operationId,
                                ["node_id"] =
                                    node.NodeId,
                                ["old_grid_x"] =
                                    oldGridX.ToString(),
                                ["old_grid_y"] =
                                    oldGridY.ToString(),
                                ["grid_x"] =
                                    gridX.ToString(),
                                ["grid_y"] =
                                    gridY.ToString()
                            }));

                    m_EventBus.Publish(
                        new NexEvent(
                            "region.control.move.requested",
                            "nexverse.world-api",
                            new Dictionary<string, string>
                            {
                                ["operation_id"] =
                                    operationId,
                                ["target_node_id"] =
                                    node.NodeId,
                                ["region_id"] =
                                    regionId.ToString(),
                                ["grid_x"] =
                                    gridX.ToString(),
                                ["grid_y"] =
                                    gridY.ToString()
                            },
                            correlationId));

                    WriteJson(
                        response,
                        new
                        {
                            operation =
                                OperationPayload(
                                    operation),
                            placement = new
                            {
                                region_id =
                                    regionId.ToString(),
                                node_id =
                                    node.NodeId,
                                old_grid_x =
                                    oldGridX,
                                old_grid_y =
                                    oldGridY,
                                grid_x =
                                    gridX,
                                grid_y =
                                    gridY,
                                size_x =
                                    sizeX,
                                size_y =
                                    sizeY
                            },
                            correlation_id =
                                correlationId
                        },
                        HttpStatusCode.Accepted);

                    CompleteIdempotency(
                        response,
                        reservation);
                    completed = true;
                }
                finally
                {
                    if (!completed)
                        AbortIdempotency(
                            reservation);
                }
            }
        }

        private void HandleOperation(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string operationId)
        {
            if (!RequireMethod(
                    request,
                    response,
                    "GET"))
            {
                return;
            }

            if (!Authenticate(
                    request,
                    response,
                    NexScopes.RegionsRead,
                    out NexPrincipal _))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    operationId) ||
                operationId.Contains('/'))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_operation_id",
                    "A single region operation ID is required.");
                return;
            }

            NexRegionOperationSnapshot operation =
                m_Operations.Get(
                    operationId);

            if (operation == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "region_operation_not_found",
                    "The region operation is not present in the current Robust operation registry.");
                return;
            }

            string correlationId =
                Correlation(response);

            WriteJson(
                response,
                new
                {
                    operation =
                        OperationPayload(
                            operation),
                    correlation_id =
                        correlationId
                });
        }

        private bool RequireMutationInfrastructure(
            IOSHttpResponse response)
        {
            if (m_Grid == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "grid_service_unavailable",
                    "The GridService is not available to the NexVerse World API.");
                return false;
            }

            if (!m_DistributedTransportEnabled)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "nexbus_transport_disabled",
                    "Managed region mutations require the distributed NexBus transport.");
                return false;
            }

            return true;
        }

        private bool ValidateTargetNode(
            IOSHttpResponse response,
            NexNodeSnapshot node)
        {
            if (node == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "node_not_found",
                    "The requested simulator node is not present in the NodeAgent registry.");
                return false;
            }

            if (!string.Equals(
                    node.State,
                    "online",
                    StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "node_not_online",
                    "Managed region mutations require an online simulator node.");
                return false;
            }

            if (!node.ManagedRegionCommands)
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "managed_region_commands_disabled",
                    "The simulator node does not advertise managed-region command capability.");
                return false;
            }

            return true;
        }

        private bool TryValidatePlacement(
            int gridX,
            int gridY,
            int sizeX,
            int sizeY,
            UUID excludedRegionId,
            out string reason,
            out GridRegion[] conflicts)
        {
            conflicts =
                Array.Empty<GridRegion>();

            if (IsHypergridReservedBand(
                    gridY))
            {
                reason =
                    "hypergrid_reserved_band";
                return false;
            }

            int cellsX =
                sizeX /
                (int)Constants.RegionSize;
            int cellsY =
                sizeY /
                (int)Constants.RegionSize;
            int maxX =
                gridX +
                cellsX -
                1;
            int maxY =
                gridY +
                cellsY -
                1;

            conflicts =
                LoadRegions(
                    gridX,
                    maxX,
                    gridY,
                    maxY)
                    .Where(region =>
                        (excludedRegionId.IsZero() ||
                         region.RegionID !=
                         excludedRegionId) &&
                        Footprint(region)
                            .Intersects(
                                gridX,
                                maxX,
                                gridY,
                                maxY))
                    .ToArray();

            if (conflicts.Length > 0)
            {
                reason =
                    "region_overlap";
                return false;
            }

            reason =
                "placement_available";
            return true;
        }

        private void WritePlacementConflict(
            IOSHttpResponse response,
            string reason,
            IEnumerable<GridRegion> conflicts)
        {
            string correlationId =
                Correlation(response);

            WriteJson(
                response,
                new
                {
                    error =
                        "region_placement_conflict",
                    reason,
                    conflicts =
                        (conflicts ??
                         Array.Empty<GridRegion>())
                            .Select(region => new
                            {
                                region_id =
                                    region.RegionID.ToString(),
                                name =
                                    region.RegionName,
                                grid_x =
                                    FloorDiv(
                                        region.RegionLocX,
                                        (int)Constants.RegionSize),
                                grid_y =
                                    FloorDiv(
                                        region.RegionLocY,
                                        (int)Constants.RegionSize),
                                size_x =
                                    NormalizeRegionSize(
                                        region.RegionSizeX),
                                size_y =
                                    NormalizeRegionSize(
                                        region.RegionSizeY)
                            })
                            .ToArray(),
                    correlation_id =
                        correlationId
                },
                HttpStatusCode.Conflict);
        }

        private List<GridRegion> LoadRegions(
            int minX,
            int maxX,
            int minY,
            int maxY)
        {
            return m_Grid.GetRegionRange(
                       UUID.Zero,
                       GridToWorld(minX),
                       CellWorldMax(maxX),
                       GridToWorld(minY),
                       CellWorldMax(maxY)) ??
                   new List<GridRegion>();
        }

        private static GridFootprint Footprint(
            GridRegion region)
        {
            int sizeX =
                NormalizeRegionSize(
                    region.RegionSizeX);
            int sizeY =
                NormalizeRegionSize(
                    region.RegionSizeY);

            long maxWorldX =
                (long)region.RegionLocX +
                sizeX -
                1L;
            long maxWorldY =
                (long)region.RegionLocY +
                sizeY -
                1L;

            return new GridFootprint(
                FloorDiv(
                    region.RegionLocX,
                    (int)Constants.RegionSize),
                FloorDiv(
                    maxWorldX,
                    (int)Constants.RegionSize),
                FloorDiv(
                    region.RegionLocY,
                    (int)Constants.RegionSize),
                FloorDiv(
                    maxWorldY,
                    (int)Constants.RegionSize));
        }

        private static bool TryValidateGeometry(
            IOSHttpResponse response,
            int gridX,
            int gridY,
            int sizeX,
            int sizeY)
        {
            if (gridX < 0 ||
                gridY < 0 ||
                gridX > MaxGridCoordinate ||
                gridY > MaxGridCoordinate)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "grid_coordinate_out_of_range",
                    "grid_x and grid_y must be valid non-negative OpenSim grid coordinates.");
                return false;
            }

            if (!IsValidRegionSize(sizeX) ||
                !IsValidRegionSize(sizeY))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_region_size",
                    "Region dimensions must be 256m multiples between 256m and 4096m.");
                return false;
            }

            int cellsX =
                sizeX /
                (int)Constants.RegionSize;
            int cellsY =
                sizeY /
                (int)Constants.RegionSize;

            if ((long)gridX +
                    cellsX -
                    1L >
                MaxGridCoordinate ||
                (long)gridY +
                    cellsY -
                    1L >
                MaxGridCoordinate)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "grid_coordinate_out_of_range",
                    "The proposed region footprint exceeds the supported world-coordinate range.");
                return false;
            }

            return true;
        }

        private bool TryReserveIdempotency(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string subject,
            string method,
            string path,
            JsonElement root,
            out IdempotencyReservation reservation)
        {
            reservation =
                null;

            string key =
                request?.Headers?[
                    "Idempotency-Key"]
                    ?.Trim();

            if (string.IsNullOrWhiteSpace(
                    key))
            {
                return true;
            }

            if (key.Length > 128)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_idempotency_key",
                    "Idempotency-Key may contain at most 128 characters.");
                return false;
            }

            if (m_Idempotency == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "idempotency_unavailable",
                    "Persistent idempotency storage is not available.");
                return false;
            }

            string canonical =
                JsonSerializer.Serialize(
                    root);

            string hash =
                Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                canonical)))
                    .ToLowerInvariant();

            string scope =
                subject +
                "|" +
                method +
                "|" +
                path;

            NexIdempotencyBeginResult begin =
                m_Idempotency.TryBegin(
                    scope,
                    key,
                    hash,
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
                return false;
            }

            if (begin.State ==
                NexIdempotencyBeginState.Conflict)
            {
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "idempotency_key_conflict",
                    "Idempotency-Key was already used with a different request payload.");
                return false;
            }

            if (begin.State ==
                NexIdempotencyBeginState.InProgress)
            {
                response.AddHeader(
                    "Retry-After",
                    "1");
                WriteError(
                    response,
                    HttpStatusCode.Conflict,
                    "idempotency_in_progress",
                    "A request with this Idempotency-Key is already in progress.");
                return false;
            }

            response.AddHeader(
                "Idempotency-Replayed",
                "false");

            reservation =
                new IdempotencyReservation(
                    scope,
                    key,
                    hash);

            return true;
        }

        private void CompleteIdempotency(
            IOSHttpResponse response,
            IdempotencyReservation reservation)
        {
            if (reservation == null ||
                m_Idempotency == null)
            {
                return;
            }

            m_Idempotency.Complete(
                reservation.Scope,
                reservation.Key,
                reservation.Hash,
                response.StatusCode,
                response.ContentType,
                response.RawBuffer,
                m_IdempotencyTtlSeconds);
        }

        private void AbortIdempotency(
            IdempotencyReservation reservation)
        {
            if (reservation == null ||
                m_Idempotency == null)
            {
                return;
            }

            m_Idempotency.Abort(
                reservation.Scope,
                reservation.Key,
                reservation.Hash);
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string requiredScope,
            out NexPrincipal principal)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    requiredScope,
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
                "Authentication or authorization failed.");

            return false;
        }

        private static bool RequireMethod(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string method)
        {
            if (request != null &&
                string.Equals(
                    request.HttpMethod,
                    method,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                method +
                " is required.");

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
                    document = null;
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
                document = null;
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

        private static bool TryGetRequiredInt(
            JsonElement root,
            string name,
            out int value)
        {
            value = 0;

            return
                root.TryGetProperty(
                    name,
                    out JsonElement element) &&
                element.ValueKind ==
                    JsonValueKind.Number &&
                element.TryGetInt32(
                    out value);
        }

        private static int GetOptionalInt(
            JsonElement root,
            string name,
            int defaultValue)
        {
            if (!root.TryGetProperty(
                    name,
                    out JsonElement element))
            {
                return defaultValue;
            }

            return
                element.ValueKind ==
                    JsonValueKind.Number &&
                element.TryGetInt32(
                    out int value)
                    ? value
                    : int.MinValue;
        }

        private static bool IsSafeRegionName(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Trim().Length > 128)
            {
                return false;
            }

            return name.IndexOfAny(
                       new[]
                       {
                           '\r',
                           '\n',
                           '[',
                           ']'
                       }) < 0;
        }

        private static bool IsValidRegionSize(
            int size)
        {
            int cellSize =
                (int)Constants.RegionSize;

            return
                size >= cellSize &&
                size <=
                    (int)Constants.MaximumRegionSize &&
                size % cellSize == 0;
        }

        private static bool IsHypergridReservedBand(
            int gridY)
        {
            return
                GridToWorld(gridY) <=
                (int)Constants.MaximumRegionSize;
        }

        private static int NormalizeRegionSize(
            int size)
        {
            return
                size > 0
                    ? size
                    : (int)Constants.RegionSize;
        }

        private static int FloorDiv(
            long value,
            int divisor)
        {
            long quotient =
                value /
                divisor;
            long remainder =
                value %
                divisor;

            if (remainder != 0 &&
                value < 0)
            {
                quotient--;
            }

            return checked(
                (int)quotient);
        }

        private static int GridToWorld(
            int gridCoordinate)
        {
            return checked(
                gridCoordinate *
                (int)Constants.RegionSize);
        }

        private static int CellWorldMax(
            int gridCoordinate)
        {
            return checked(
                GridToWorld(
                    gridCoordinate) +
                (int)Constants.RegionSize -
                1);
        }

        private static int MaxGridCoordinate =>
            (int.MaxValue -
             (int)Constants.MaximumRegionSize) /
            (int)Constants.RegionSize;

        private static object OperationPayload(
            NexRegionOperationSnapshot operation)
        {
            return new
            {
                operation_id =
                    operation.OperationId,
                operation =
                    operation.Operation,
                state =
                    operation.State,
                message =
                    operation.Message,
                node_id =
                    operation.NodeId,
                region_id =
                    operation.RegionId,
                created_at =
                    operation.CreatedAt,
                updated_at =
                    operation.UpdatedAt,
                details =
                    operation.Details
            };
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
                Correlation(response);

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
            response.KeepAlive = false;
            response.StatusCode =
                (int)status;
            response.ContentType =
                "application/json; charset=utf-8";
            response.AddHeader(
                "Cache-Control",
                "no-store");
            response.AddHeader(
                "X-Content-Type-Options",
                "nosniff");
            response.RawBuffer =
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        payload,
                        s_Json));
        }

        private sealed class IdempotencyReservation
        {
            public IdempotencyReservation(
                string scope,
                string key,
                string hash)
            {
                Scope = scope;
                Key = key;
                Hash = hash;
            }

            public string Scope { get; }
            public string Key { get; }
            public string Hash { get; }
        }

        private readonly struct GridFootprint
        {
            public GridFootprint(
                int minX,
                int maxX,
                int minY,
                int maxY)
            {
                MinX = minX;
                MaxX = maxX;
                MinY = minY;
                MaxY = maxY;
            }

            public int MinX { get; }
            public int MaxX { get; }
            public int MinY { get; }
            public int MaxY { get; }

            public bool Intersects(
                int minX,
                int maxX,
                int minY,
                int maxY)
            {
                return
                    MinX <= maxX &&
                    MaxX >= minX &&
                    MinY <= maxY &&
                    MaxY >= minY;
            }
        }
    }
}
