// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Public, read-only map projection for the NexVerse citizen portal.
    /// Unlike /grid/layout this never exposes simulator endpoints, node IDs,
    /// reservation records, owners or administrative placement metadata.
    /// Coordinates are expressed in 256-meter grid cells, not meters.
    /// </summary>
    internal sealed class NexCitizenWorldMapApi
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(NexCitizenWorldMapApi));
        private const int CellMeters = 256;
        private const int MaxWindowAxis = 16;
        private const int MaxCellCoordinate = (int.MaxValue - (CellMeters - 1)) / CellMeters;
        private readonly IGridService m_Grid;
        private readonly string m_TeleportBaseUri;

        public NexCitizenWorldMapApi(IGridService grid, string teleportBaseUri)
        {
            m_Grid = grid;
            m_TeleportBaseUri = (teleportBaseUri ?? string.Empty).TrimEnd('/');
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!string.Equals(request?.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                response.AddHeader("Allow", "GET");
                Json(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
                return;
            }

            if ((request.UriPath ?? string.Empty).TrimEnd('/').Equals(
                "/api/v1/world-map/view", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "text/html; charset=utf-8";
                response.AddHeader("Cache-Control", "no-store");
                response.AddHeader("X-Content-Type-Options", "nosniff");
                response.RawBuffer = Encoding.UTF8.GetBytes(NexCitizenWorldMapPage.Html);
                return;
            }

            if (!(request.UriPath ?? string.Empty).TrimEnd('/').Equals(
                "/api/v1/world-map", StringComparison.OrdinalIgnoreCase))
            {
                Json(response, HttpStatusCode.NotFound, new { error = "not_found" });
                return;
            }

            if (m_Grid == null)
            {
                Json(response, HttpStatusCode.ServiceUnavailable, new { error = "grid_service_unavailable" });
                return;
            }

            try
            {
                string search = (request.QueryString?["q"] ?? string.Empty).Trim();
                if (search.Length > 80)
                {
                    Json(response, HttpStatusCode.BadRequest, new { error = "invalid_query", message = "q must be at most 80 characters." });
                    return;
                }

                if (!string.IsNullOrEmpty(search))
                {
                    if (search.Length < 2)
                    {
                        Json(response, HttpStatusCode.BadRequest, new { error = "invalid_query", message = "q must contain at least two characters." });
                        return;
                    }
                    GridRegion[] matches = (m_Grid.GetRegionsByName(UUID.Zero, search, 32) ?? new List<GridRegion>())
                        .Where(Visible).Take(32).ToArray();
                    Json(response, HttpStatusCode.OK, new
                    {
                        cell_size_meters = CellMeters,
                        mode = "search",
                        query = search,
                        count = matches.Length,
                        regions = matches.Select(PublicRegion).ToArray(),
                        correlation_id = NexApiRequestContext.Ensure(response)
                    });
                    return;
                }

                bool anyBounds = new[] { "min_x", "max_x", "min_y", "max_y" }
                    .Any(key => request.QueryString?[key] != null);
                int minX, maxX, minY, maxY;
                if (anyBounds)
                {
                    if (!Cell(request, "min_x", out minX) || !Cell(request, "max_x", out maxX) ||
                        !Cell(request, "min_y", out minY) || !Cell(request, "max_y", out maxY) ||
                        minX > maxX || minY > maxY || maxX - minX + 1 > MaxWindowAxis ||
                        maxY - minY + 1 > MaxWindowAxis)
                    {
                        Json(response, HttpStatusCode.BadRequest, new
                        {
                            error = "invalid_map_bounds",
                            message = "Provide all four non-negative grid-cell bounds, each axis spanning at most 16 cells."
                        });
                        return;
                    }
                }
                else
                {
                    GridRegion initial = (m_Grid.GetDefaultRegions(UUID.Zero) ?? new List<GridRegion>())
                        .FirstOrDefault(Visible);
                    if (initial == null)
                    {
                        Json(response, HttpStatusCode.OK, new
                        {
                            cell_size_meters = CellMeters,
                            mode = "viewport",
                            has_default_region = false,
                            tile_url_template = "/map/map-1-{x}-{y}-objects.jpg",
                            regions = Array.Empty<object>(),
                            correlation_id = NexApiRequestContext.Ensure(response)
                        });
                        return;
                    }
                    int centerX = initial.RegionLocX / CellMeters;
                    int centerY = initial.RegionLocY / CellMeters;
                    minX = Math.Max(0, centerX - 5);
                    minY = Math.Max(0, centerY - 5);
                    maxX = Math.Min(MaxCellCoordinate, minX + 11);
                    maxY = Math.Min(MaxCellCoordinate, minY + 11);
                }

                int fromX = checked(minX * CellMeters);
                int fromY = checked(minY * CellMeters);
                int toX = checked(maxX * CellMeters + CellMeters - 1);
                int toY = checked(maxY * CellMeters + CellMeters - 1);
                GridRegion[] regions = (m_Grid.GetRegionRange(UUID.Zero, fromX, toX, fromY, toY) ??
                    new List<GridRegion>()).Where(Visible)
                    .GroupBy(r => r.RegionID).Select(g => g.First())
                    .Take(256).ToArray();

                Json(response, HttpStatusCode.OK, new
                {
                    cell_size_meters = CellMeters,
                    mode = "viewport",
                    has_default_region = true,
                    bounds = new { min_x = minX, max_x = maxX, min_y = minY, max_y = maxY },
                    tile_url_template = "/map/map-1-{x}-{y}-objects.jpg",
                    regions = regions.Select(PublicRegion).ToArray(),
                    count = regions.Length,
                    correlation_id = NexApiRequestContext.Ensure(response)
                });
            }
            catch (Exception e)
            {
                m_Log.Error("[NEX-WORLD-MAP]: Failed to resolve public map viewport.", e);
                Json(response, HttpStatusCode.ServiceUnavailable, new { error = "world_map_unavailable" });
            }
        }

        private static bool Cell(IOSHttpRequest request, string name, out int value)
        {
            value = 0;
            return int.TryParse(request.QueryString?[name], out value) &&
                   value >= 0 && value <= MaxCellCoordinate;
        }

        private static bool Visible(GridRegion region)
        {
            if (region == null || region.RegionID.IsZero() ||
                string.IsNullOrWhiteSpace(region.RegionName))
                return false;

            if (region.RegionFlags.HasValue)
            {
                int flags = (int)region.RegionFlags.Value;
                if ((flags & (int)OpenSim.Framework.RegionFlags.Reservation) != 0 ||
                    (flags & (int)OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                    return false;
            }
            return true;
        }

        private object PublicRegion(GridRegion region)
        {
            int sizeX = region.RegionSizeX > 0 ? region.RegionSizeX : CellMeters;
            int sizeY = region.RegionSizeY > 0 ? region.RegionSizeY : CellMeters;
            return new
            {
                region_id = region.RegionID.ToString(),
                name = region.RegionName,
                grid_x = region.RegionLocX / CellMeters,
                grid_y = region.RegionLocY / CellMeters,
                cells_x = Math.Max(1, (sizeX + CellMeters - 1) / CellMeters),
                cells_y = Math.Max(1, (sizeY + CellMeters - 1) / CellMeters),
                size_x = sizeX,
                size_y = sizeY,
                maturity = Math.Max(0, Math.Min(2, region.Maturity)),
                teleport_uri = TeleportUri(region.RegionName)
            };
        }

        private string TeleportUri(string name)
        {
            if (string.IsNullOrWhiteSpace(m_TeleportBaseUri) ||
                !Uri.TryCreate(m_TeleportBaseUri, UriKind.Absolute, out Uri root) ||
                (root.Scheme != "http" && root.Scheme != "https" &&
                 root.Scheme != "hop" && root.Scheme != "secondlife"))
                return string.Empty;

            return m_TeleportBaseUri + "/" + Uri.EscapeDataString(name) + "/128/128/25";
        }

        private static void Json(IOSHttpResponse response, HttpStatusCode status, object payload)
        {
            response.KeepAlive = false;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.AddHeader("Cache-Control", "public, max-age=20");
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload);
        }
    }
}
