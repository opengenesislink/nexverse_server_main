// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexEstateApi
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly IEstateDataService m_Estates;
        private readonly NexApiAuthenticator m_Authenticator;

        public NexEstateApi(
            IEstateDataService estates,
            NexApiAuthenticator authenticator)
        {
            m_Estates = estates;
            m_Authenticator =
                authenticator ??
                throw new ArgumentNullException(nameof(authenticator));
        }

        public void Handle(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!RequireGet(
                    request,
                    response))
            {
                return;
            }

            if (!Authenticate(
                    request,
                    response))
            {
                return;
            }

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
                HandleList(
                    request,
                    response);
                return;
            }

            const string prefix =
                "/api/v1/estates/";

            if (path.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                string rawId =
                    path.Substring(
                        prefix.Length);

                if (!int.TryParse(
                        rawId,
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

                HandleGet(
                    response,
                    estateId);
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
                            ordered.Length
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
                return;
            }

            string correlationId =
                NexApiRequestContext.Ensure(
                    response);

            WriteJson(
                response,
                new
                {
                    estate =
                        EstatePayload(
                            estate),
                    correlation_id =
                        correlationId
                });
        }

        private object EstatePayload(
            EstateSettings estate)
        {
            int regionCount =
                0;

            try
            {
                regionCount =
                    m_Estates
                        .GetRegions(
                            (int)estate.EstateID)
                        ?.Count ??
                    0;
            }
            catch
            {
                regionCount =
                    0;
            }

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
                    regionCount
            };
        }

        private bool Authenticate(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (m_Authenticator.TryAuthenticate(
                    request,
                    NexScopes.EstatesRead,
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
                "Authentication or estates:read authorization is required.");

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

        private static void WriteError(
            IOSHttpResponse response,
            HttpStatusCode status,
            string error,
            string message)
        {
            string correlationId =
                NexApiRequestContext.Ensure(
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
    }
}
