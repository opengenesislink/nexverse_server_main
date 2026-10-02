// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using log4net;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Read-only Region Control Plane foundation.
    /// Grid coordinates are expressed in OpenSim base cells (256m).
    /// </summary>
    internal sealed class NexGridControlApi
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexGridControlApi));

        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private const int MaxViewportAxisCells = 128;

        private readonly IGridService m_Grid;
        private readonly NexApiAuthenticator m_Authenticator;

        public NexGridControlApi(
            IGridService grid,
            NexApiAuthenticator authenticator)
        {
            m_Grid = grid;
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            string path =
                (request?.UriPath ?? string.Empty)
                    .TrimEnd('/');

            if (!RequireGet(request, response))
                return;

            if (!Authenticate(request, response))
                return;

            if (m_Grid == null)
            {
                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "grid_service_unavailable",
                    "The GridService is not available to the NexVerse World API.");
                return;
            }

            try
            {
                if (string.Equals(
                        path,
                        "/api/v1/grid/layout",
                        StringComparison.OrdinalIgnoreCase))
                {
                    HandleLayout(request, response);
                    return;
                }

                if (string.Equals(
                        path,
                        "/api/v1/grid/validate-placement",
                        StringComparison.OrdinalIgnoreCase))
                {
                    HandleValidatePlacement(request, response);
                    return;
                }

                const string cellPrefix =
                    "/api/v1/grid/cells/";

                if (path.StartsWith(
                        cellPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    HandleCell(
                        response,
                        path.Substring(cellPrefix.Length));
                    return;
                }

                WriteError(
                    response,
                    HttpStatusCode.NotFound,
                    "not_found",
                    "Unknown NexVerse grid-control endpoint.");
            }
            catch (Exception e)
            {
                m_Log.Error(
                    "[NEX-GRID-CONTROL]: Grid-control request failed.",
                    e);

                WriteError(
                    response,
                    HttpStatusCode.ServiceUnavailable,
                    "grid_control_unavailable",
                    "Grid-control data could not be calculated from the current GridService state.");
            }
        }

        private void HandleLayout(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!TryReadGridCoordinate(
                    request,
                    response,
                    "min_x",
                    out int minX) ||
                !TryReadGridCoordinate(
                    request,
                    response,
                    "max_x",
                    out int maxX) ||
                !TryReadGridCoordinate(
                    request,
                    response,
                    "min_y",
                    out int minY) ||
                !TryReadGridCoordinate(
                    request,
                    response,
                    "max_y",
                    out int maxY))
            {
                return;
            }

            if (maxX < minX || maxY < minY)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_grid_bounds",
                    "max_x/max_y must be greater than or equal to min_x/min_y.");
                return;
            }

            long widthCells =
                (long)maxX - minX + 1L;
            long heightCells =
                (long)maxY - minY + 1L;

            if (widthCells > MaxViewportAxisCells ||
                heightCells > MaxViewportAxisCells)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "grid_window_too_large",
                    "A grid-layout viewport may span at most " +
                    MaxViewportAxisCells +
                    " cells on each axis.");
                return;
            }

            List<GridRegion> regions =
                LoadRegions(
                    minX,
                    maxX,
                    minY,
                    maxY);

            Dictionary<long, List<GridRegion>> occupancy =
                BuildOccupancy(
                    regions,
                    minX,
                    maxX,
                    minY,
                    maxY);

            List<object> cells =
                new List<object>(
                    checked((int)(widthCells * heightCells)));

            int freeCells = 0;
            int occupiedCells = 0;
            int reservedCells = 0;
            int conflictCells = 0;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    long key = CellKey(x, y);

                    if (occupancy.TryGetValue(
                            key,
                            out List<GridRegion> cellRegions) &&
                        cellRegions.Count > 0)
                    {
                        bool reservation =
                            cellRegions.Any(IsReservation);

                        string status =
                            cellRegions.Count > 1
                                ? "conflict"
                                : reservation
                                    ? "reserved"
                                    : "occupied";

                        if (status == "conflict")
                            conflictCells++;
                        else if (status == "reserved")
                            reservedCells++;
                        else
                            occupiedCells++;

                        cells.Add(
                            CellPayload(
                                x,
                                y,
                                status,
                                cellRegions));
                    }
                    else if (IsHypergridReservedBand(y))
                    {
                        reservedCells++;

                        cells.Add(
                            CellPayload(
                                x,
                                y,
                                "reserved",
                                Array.Empty<GridRegion>()));
                    }
                    else
                    {
                        freeCells++;

                        cells.Add(
                            CellPayload(
                                x,
                                y,
                                "free",
                                Array.Empty<GridRegion>()));
                    }
                }
            }

            string correlationId =
                Correlation(response);

            WriteJson(response, new
            {
                cell_size_meters =
                    (int)Constants.RegionSize,
                bounds = new
                {
                    min_x = minX,
                    max_x = maxX,
                    min_y = minY,
                    max_y = maxY,
                    min_world_x = GridToWorld(minX),
                    max_world_x = CellWorldMax(maxX),
                    min_world_y = GridToWorld(minY),
                    max_world_y = CellWorldMax(maxY)
                },
                counts = new
                {
                    cells =
                        checked((int)(widthCells * heightCells)),
                    free = freeCells,
                    occupied = occupiedCells,
                    reserved = reservedCells,
                    conflict = conflictCells,
                    regions = regions.Count
                },
                regions =
                    regions
                        .OrderBy(x => x.RegionLocY)
                        .ThenBy(x => x.RegionLocX)
                        .Select(RegionPayload)
                        .ToArray(),
                cells = cells.ToArray(),
                correlation_id = correlationId
            });
        }

        private void HandleCell(
            IOSHttpResponse response,
            string coordinates)
        {
            string[] parts =
                (coordinates ?? string.Empty)
                    .Split(
                        '/',
                        StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2 ||
                !TryParseGridCoordinate(
                    parts[0],
                    out int x) ||
                !TryParseGridCoordinate(
                    parts[1],
                    out int y))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_grid_cell",
                    "Cell coordinates must be non-negative integer grid coordinates.");
                return;
            }

            List<GridRegion> regions =
                LoadRegions(
                    x,
                    x,
                    y,
                    y);

            List<GridRegion> occupying =
                regions
                    .Where(region =>
                        Footprint(region).Contains(x, y))
                    .ToList();

            string status;

            if (occupying.Count > 1)
                status = "conflict";
            else if (occupying.Any(IsReservation) ||
                     (occupying.Count == 0 &&
                      IsHypergridReservedBand(y)))
                status = "reserved";
            else if (occupying.Count == 1)
                status = "occupied";
            else
                status = "free";

            string correlationId =
                Correlation(response);

            WriteJson(response, new
            {
                cell =
                    CellPayload(
                        x,
                        y,
                        status,
                        occupying),
                correlation_id = correlationId
            });
        }

        private void HandleValidatePlacement(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!TryReadGridCoordinate(
                    request,
                    response,
                    "x",
                    out int x) ||
                !TryReadGridCoordinate(
                    request,
                    response,
                    "y",
                    out int y) ||
                !TryReadPositiveInt(
                    request,
                    response,
                    "size_x",
                    out int sizeX) ||
                !TryReadPositiveInt(
                    request,
                    response,
                    "size_y",
                    out int sizeY))
            {
                return;
            }

            if (!IsValidRegionSize(sizeX) ||
                !IsValidRegionSize(sizeY))
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "invalid_region_size",
                    "size_x and size_y must be multiples of 256m between 256m and " +
                    Constants.MaximumRegionSize +
                    "m.");
                return;
            }

            int cellsX =
                sizeX / (int)Constants.RegionSize;
            int cellsY =
                sizeY / (int)Constants.RegionSize;

            if ((long)x + cellsX - 1L > MaxGridCoordinate ||
                (long)y + cellsY - 1L > MaxGridCoordinate)
            {
                WriteError(
                    response,
                    HttpStatusCode.BadRequest,
                    "grid_coordinate_out_of_range",
                    "The proposed region footprint exceeds the supported OpenSim world-coordinate range.");
                return;
            }

            int maxX =
                x + cellsX - 1;
            int maxY =
                y + cellsY - 1;

            List<GridRegion> conflicts =
                LoadRegions(
                    x,
                    maxX,
                    y,
                    maxY)
                    .Where(region =>
                        Footprint(region)
                            .Intersects(
                                x,
                                maxX,
                                y,
                                maxY))
                    .ToList();

            bool reservedBand =
                IsHypergridReservedBand(y);

            bool valid =
                !reservedBand &&
                conflicts.Count == 0;

            string correlationId =
                Correlation(response);

            WriteJson(response, new
            {
                valid,
                reason =
                    valid
                        ? "placement_available"
                        : reservedBand
                            ? "hypergrid_reserved_band"
                            : "region_overlap",
                origin = new
                {
                    grid_x = x,
                    grid_y = y,
                    world_x = GridToWorld(x),
                    world_y = GridToWorld(y)
                },
                size = new
                {
                    size_x = sizeX,
                    size_y = sizeY,
                    cells_x = cellsX,
                    cells_y = cellsY
                },
                footprint = new
                {
                    min_x = x,
                    max_x = maxX,
                    min_y = y,
                    max_y = maxY,
                    min_world_x = GridToWorld(x),
                    max_world_x =
                        checked(GridToWorld(x) + sizeX - 1),
                    min_world_y = GridToWorld(y),
                    max_world_y =
                        checked(GridToWorld(y) + sizeY - 1)
                },
                conflicts =
                    conflicts
                        .Select(RegionPayload)
                        .ToArray(),
                correlation_id = correlationId
            });
        }

        private List<GridRegion> LoadRegions(
            int minX,
            int maxX,
            int minY,
            int maxY)
        {
            int minWorldX =
                GridToWorld(minX);
            int minWorldY =
                GridToWorld(minY);
            int maxWorldX =
                CellWorldMax(maxX);
            int maxWorldY =
                CellWorldMax(maxY);

            return m_Grid.GetRegionRange(
                       UUID.Zero,
                       minWorldX,
                       maxWorldX,
                       minWorldY,
                       maxWorldY) ??
                   new List<GridRegion>();
        }

        private static Dictionary<long, List<GridRegion>>
            BuildOccupancy(
                IEnumerable<GridRegion> regions,
                int minX,
                int maxX,
                int minY,
                int maxY)
        {
            Dictionary<long, List<GridRegion>> result =
                new Dictionary<long, List<GridRegion>>();

            foreach (GridRegion region in regions)
            {
                GridFootprint footprint =
                    Footprint(region);

                int startX =
                    Math.Max(minX, footprint.MinX);
                int endX =
                    Math.Min(maxX, footprint.MaxX);
                int startY =
                    Math.Max(minY, footprint.MinY);
                int endY =
                    Math.Min(maxY, footprint.MaxY);

                if (startX > endX ||
                    startY > endY)
                {
                    continue;
                }

                for (int y = startY; y <= endY; y++)
                {
                    for (int x = startX; x <= endX; x++)
                    {
                        long key =
                            CellKey(x, y);

                        if (!result.TryGetValue(
                                key,
                                out List<GridRegion> cellRegions))
                        {
                            cellRegions =
                                new List<GridRegion>();

                            result[key] =
                                cellRegions;
                        }

                        if (!cellRegions.Any(existing =>
                                existing.RegionID ==
                                region.RegionID))
                        {
                            cellRegions.Add(region);
                        }
                    }
                }
            }

            return result;
        }

        private static object CellPayload(
            int x,
            int y,
            string status,
            IEnumerable<GridRegion> regions)
        {
            GridRegion[] occupying =
                regions?.ToArray() ??
                Array.Empty<GridRegion>();

            return new
            {
                grid_x = x,
                grid_y = y,
                world_x = GridToWorld(x),
                world_y = GridToWorld(y),
                status,
                region_id =
                    occupying.Length == 1
                        ? occupying[0].RegionID.ToString()
                        : null,
                region_name =
                    occupying.Length == 1
                        ? occupying[0].RegionName
                        : null,
                region_ids =
                    occupying
                        .Select(region =>
                            region.RegionID.ToString())
                        .ToArray()
            };
        }

        private static object RegionPayload(
            GridRegion region)
        {
            GridFootprint footprint =
                Footprint(region);

            bool? online = null;

            if (region.RegionFlags.HasValue)
            {
                online =
                    (region.RegionFlags.Value &
                     OpenSim.Framework.RegionFlags.RegionOnline) != 0;
            }

            return new
            {
                region_id =
                    region.RegionID.ToString(),
                name =
                    region.RegionName,
                grid_x =
                    footprint.MinX,
                grid_y =
                    footprint.MinY,
                world_x =
                    region.RegionLocX,
                world_y =
                    region.RegionLocY,
                size_x =
                    NormalizedRegionSize(region.RegionSizeX),
                size_y =
                    NormalizedRegionSize(region.RegionSizeY),
                cells_x =
                    footprint.MaxX -
                    footprint.MinX +
                    1,
                cells_y =
                    footprint.MaxY -
                    footprint.MinY +
                    1,
                occupied = new
                {
                    min_x =
                        footprint.MinX,
                    max_x =
                        footprint.MaxX,
                    min_y =
                        footprint.MinY,
                    max_y =
                        footprint.MaxY
                },
                online,
                reserved =
                    IsReservation(region),
                server_uri =
                    region.ServerURI
            };
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    NexScopes.RegionsRead,
                    out NexPrincipal _,
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
                "Authentication or regions:read authorization is required.");

            return false;
        }

        private static bool RequireGet(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (request != null &&
                string.Equals(
                    request.HttpMethod,
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            WriteError(
                response,
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                "GET is required.");

            return false;
        }

        private static bool TryReadGridCoordinate(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string name,
            out int value)
        {
            string raw =
                request?.QueryString?[name];

            if (TryParseGridCoordinate(
                    raw,
                    out value))
            {
                return true;
            }

            WriteError(
                response,
                HttpStatusCode.BadRequest,
                "invalid_grid_coordinate",
                name +
                " must be a non-negative integer grid coordinate.");

            return false;
        }

        private static bool TryParseGridCoordinate(
            string raw,
            out int value)
        {
            if (!int.TryParse(
                    raw,
                    out value) ||
                value < 0 ||
                value > MaxGridCoordinate)
            {
                value = 0;
                return false;
            }

            return true;
        }

        private static bool TryReadPositiveInt(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string name,
            out int value)
        {
            string raw =
                request?.QueryString?[name];

            if (int.TryParse(
                    raw,
                    out value) &&
                value > 0)
            {
                return true;
            }

            value = 0;

            WriteError(
                response,
                HttpStatusCode.BadRequest,
                "invalid_integer",
                name +
                " must be a positive integer.");

            return false;
        }

        private static bool IsValidRegionSize(
            int size)
        {
            int cellSize =
                (int)Constants.RegionSize;

            return
                size >= cellSize &&
                size <= (int)Constants.MaximumRegionSize &&
                size % cellSize == 0;
        }

        private static bool IsHypergridReservedBand(
            int gridY)
        {
            return
                GridToWorld(gridY) <=
                (int)Constants.MaximumRegionSize;
        }

        private static bool IsReservation(
            GridRegion region)
        {
            return
                region != null &&
                region.RegionFlags.HasValue &&
                (region.RegionFlags.Value &
                 OpenSim.Framework.RegionFlags.Reservation) != 0;
        }

        private static int NormalizedRegionSize(
            int size)
        {
            return
                size > 0
                    ? size
                    : (int)Constants.RegionSize;
        }

        private static GridFootprint Footprint(
            GridRegion region)
        {
            int sizeX =
                NormalizedRegionSize(
                    region.RegionSizeX);
            int sizeY =
                NormalizedRegionSize(
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

        private static int FloorDiv(
            long value,
            int divisor)
        {
            long quotient =
                value / divisor;
            long remainder =
                value % divisor;

            if (remainder != 0 &&
                value < 0)
            {
                quotient--;
            }

            return checked((int)quotient);
        }

        private static long CellKey(
            int x,
            int y)
        {
            return
                ((long)x << 32) |
                (uint)y;
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
                GridToWorld(gridCoordinate) +
                (int)Constants.RegionSize -
                1);
        }

        private static int MaxGridCoordinate =>
            (int.MaxValue -
             (int)Constants.MaximumRegionSize) /
            (int)Constants.RegionSize;

        private static string Correlation(
            IOSHttpResponse response)
        {
            return
                NexApiRequestContext.Ensure(response);
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

            public bool Contains(
                int x,
                int y)
            {
                return
                    x >= MinX &&
                    x <= MaxX &&
                    y >= MinY &&
                    y <= MaxY;
            }

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
