// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexVerse.Core.Audit;
using NexVerse.Core.Discovery;
using NexVerse.Core.Economy;
using NexVerse.Core.Experiences;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Groups;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace NexVerse.Server.Api
{
    internal sealed class NexDiscoveryApi
    {
        private static readonly JsonSerializerOptions s_Json =
            CreateJsonOptions();

        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IUserAccountService m_Users;
        private readonly IGridService m_Grid;
        private readonly GroupsService m_Groups;
        private readonly NexExperienceStore m_Experiences;
        private readonly NexDiscoveryStore m_Store;
        private readonly Func<NexEconomyService> m_Economy;
        private readonly INexAuditSink m_Audit;
        private readonly string m_TeleportBaseUri;

        public NexDiscoveryApi(
            NexApiAuthenticator authenticator,
            IUserAccountService users,
            IGridService grid,
            GroupsService groups,
            NexExperienceStore experiences,
            NexDiscoveryStore store,
            Func<NexEconomyService> economy,
            INexAuditSink audit,
            string teleportBaseUri)
        {
            m_Authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            m_Users = users;
            m_Grid = grid;
            m_Groups = groups;
            m_Experiences = experiences ?? throw new ArgumentNullException(nameof(experiences));
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Economy = economy ?? throw new ArgumentNullException(nameof(economy));
            m_Audit = audit ?? NullNexAuditSink.Instance;
            m_TeleportBaseUri = (teleportBaseUri ?? string.Empty).Trim().TrimEnd('/');
        }

        public void HandleSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            try
            {
                string query = Query(request, "q");
                HashSet<string> types =
                    ParseTypes(Query(request, "types"));
                NexDiscoveryMaturity? maturity =
                    ParseOptionalMaturity(Query(request, "maturity"));
                int limit = QueryInt(request, "limit", 50, 1, 100);

                List<object> hits = new List<object>();

                if (Wants(types, "people") && query.Length > 0 && m_Users != null)
                {
                    foreach (UserAccount user in
                             (m_Users.GetUserAccounts(UUID.Zero, query) ??
                              new List<UserAccount>())
                             .Where(x => x != null && x.Active)
                             .Take(limit))
                    {
                        hits.Add(new
                        {
                            type = "people",
                            id = user.PrincipalID.ToString(),
                            name = user.EffectiveDisplayName,
                            username = user.Username,
                            description = user.UserTitle ?? string.Empty
                        });
                    }
                }

                if (Wants(types, "groups") && query.Length > 0 && m_Groups != null)
                {
                    foreach (DirGroupsReplyData group in
                             (m_Groups.FindGroups(UUID.Zero.ToString(), query) ??
                              new List<DirGroupsReplyData>())
                             .Take(limit))
                    {
                        hits.Add(new
                        {
                            type = "groups",
                            id = group.groupID.ToString(),
                            name = group.groupName,
                            members = group.members
                        });
                    }
                }

                if (Wants(types, "regions") && query.Length > 0 && m_Grid != null)
                {
                    foreach (GridRegion region in
                             (m_Grid.GetRegionsByName(UUID.Zero, query, limit) ??
                              new List<GridRegion>())
                             .Take(limit))
                    {
                        if (!AllowsMaturity(maturity, RegionMaturity(region)))
                            continue;

                        hits.Add(new
                        {
                            type = "regions",
                            id = region.RegionID.ToString(),
                            name = region.RegionName,
                            maturity = RegionMaturity(region).ToString().ToLowerInvariant(),
                            x = region.RegionLocX,
                            y = region.RegionLocY,
                            size_x = region.RegionSizeX,
                            size_y = region.RegionSizeY,
                            teleport_uri = BuildTeleport(region.RegionName, 128, 128, 25)
                        });
                    }
                }

                if (Wants(types, "experiences"))
                {
                    foreach (NexExperience experience in
                             m_Experiences.Search(query, 0, limit)
                                 .Where(x => x.Enabled))
                    {
                        NexDiscoveryMaturity experienceMaturity =
                            (NexDiscoveryMaturity)(int)experience.Maturity;
                        if (!AllowsMaturity(maturity, experienceMaturity))
                            continue;

                        hits.Add(new
                        {
                            type = "experiences",
                            id = experience.ExperienceId,
                            name = experience.Name,
                            description = experience.Description,
                            maturity = experienceMaturity.ToString().ToLowerInvariant(),
                            owner_id = experience.OwnerId,
                            group_id = experience.GroupId
                        });
                    }
                }

                AddContentHits(hits, query, types, maturity, limit);
                AddLandHits(hits, query, types, maturity, limit);

                WriteJson(response, new
                {
                    query,
                    types = types.Count == 0 ? new[] { "all" } : types.OrderBy(x => x).ToArray(),
                    count = Math.Min(hits.Count, limit),
                    results = hits.Take(limit).ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        public void HandlePlaces(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            string method = request?.HttpMethod ?? string.Empty;

            if (path.Equals("/api/v1/places", StringComparison.OrdinalIgnoreCase))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        IReadOnlyList<NexPlaceRecord> places =
                            m_Store.ListPlaces(
                                Query(request, "q"),
                                ParseOptionalMaturity(Query(request, "maturity")),
                                NexDiscoveryPublicationState.Approved,
                                QueryInt(request, "offset", 0, 0, int.MaxValue),
                                QueryInt(request, "limit", 50, 1, 200));

                        WriteJson(response, new
                        {
                            places = places.Select(PlacePayload).ToArray(),
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
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        NexPlaceRecord input = ReadBody<NexPlaceRecord>(request);
                        NexPlaceRecord saved = m_Store.UpsertPlace(input);
                        Audit(principal, "discovery.place.upserted", "place:" + saved.PlaceId, response);
                        WriteJson(response, new
                        {
                            place = PlacePayload(saved),
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

            if (TryResourceId(path, "/api/v1/places/", out Guid placeId))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    NexPlaceRecord place = m_Store.GetPlace(placeId);
                    if (place == null || place.State != NexDiscoveryPublicationState.Approved)
                    {
                        NotFound(response, "place_not_found", "Place was not found.");
                        return;
                    }

                    WriteJson(response, new
                    {
                        place = PlacePayload(place),
                        events = place.EventIds
                            .Select(m_Store.GetEvent)
                            .Where(x => x != null && x.State == NexDiscoveryPublicationState.Approved)
                            .ToArray(),
                        related_destinations = place.RelatedDestinationIds
                            .Select(m_Store.GetDestination)
                            .Where(x => x != null && x.State == NexDiscoveryPublicationState.Approved)
                            .Select(DestinationPayload)
                            .ToArray(),
                        correlation_id = Correlation(response)
                    });
                    return;
                }

                if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        NexPlaceRecord input = ReadBody<NexPlaceRecord>(request);
                        input.PlaceId = placeId;
                        NexPlaceRecord saved = m_Store.UpsertPlace(input);
                        Audit(principal, "discovery.place.upserted", "place:" + saved.PlaceId, response);
                        WriteJson(response, new { place = PlacePayload(saved), correlation_id = Correlation(response) });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }

                if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    bool removed = m_Store.DeletePlace(placeId);
                    Audit(principal, "discovery.place.deleted", "place:" + placeId, response);
                    WriteJson(response, new { deleted = removed, correlation_id = Correlation(response) });
                    return;
                }

                MethodNotAllowed(response, "GET, PUT or DELETE");
                return;
            }

            NotFound(response, "not_found", "Places endpoint was not found.");
        }

        public void HandleContent(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            string method = request?.HttpMethod ?? string.Empty;

            if (path.Equals("/api/v1/events", StringComparison.OrdinalIgnoreCase))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(response, new
                    {
                        events = m_Store.ListEvents(
                            Query(request, "q"),
                            ParseOptionalMaturity(Query(request, "maturity")),
                            NexDiscoveryPublicationState.Approved,
                            QueryInt(request, "offset", 0, 0, int.MaxValue),
                            QueryInt(request, "limit", 50, 1, 200)),
                        correlation_id = Correlation(response)
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        NexEventRecord saved = m_Store.UpsertEvent(ReadBody<NexEventRecord>(request));
                        Audit(principal, "discovery.event.upserted", "event:" + saved.EventId, response);
                        WriteJson(response, new { @event = saved, correlation_id = Correlation(response) }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }
            }

            if (path.Equals("/api/v1/classifieds", StringComparison.OrdinalIgnoreCase))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(response, new
                    {
                        classifieds = m_Store.ListClassifieds(
                            Query(request, "q"),
                            ParseOptionalMaturity(Query(request, "maturity")),
                            NexDiscoveryPublicationState.Approved,
                            QueryInt(request, "offset", 0, 0, int.MaxValue),
                            QueryInt(request, "limit", 50, 1, 200)),
                        correlation_id = Correlation(response)
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        NexClassifiedRecord saved = m_Store.UpsertClassified(ReadBody<NexClassifiedRecord>(request));
                        Audit(principal, "discovery.classified.upserted", "classified:" + saved.ClassifiedId, response);
                        WriteJson(response, new { classified = saved, correlation_id = Correlation(response) }, HttpStatusCode.Created);
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }
            }

            NotFound(response, "not_found", "Discovery content endpoint was not found.");
        }

        public void HandleLandPortal(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireMethod(request, response, "GET"))
                return;

            try
            {
                NexEconomyService economy =
                    m_Economy() ??
                    throw new InvalidOperationException("NV$ economy is unavailable.");

                string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
                string query = Query(request, "q");
                NexLandListingType? listingType =
                    ParseLandListingType(Query(request, "type"));
                int limit = QueryInt(request, "limit", 50, 1, 200);
                int offset = QueryInt(request, "offset", 0, 0, int.MaxValue);

                IReadOnlyList<NexLandListing> listings =
                    economy.SearchLandListings(query, listingType, 0, 500);

                if (path.EndsWith("/owned", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoveryRead, out NexPrincipal _, out UserAccount account))
                        return;

                    Guid ownerId =
                        account != null
                            ? account.PrincipalID.Guid
                            : QueryGuid(request, "owner_id");

                    listings =
                        listings.Where(x => x.SellerAccountId == ownerId).ToArray();
                }
                else
                {
                    listings =
                        listings.Where(x => x.Active).ToArray();
                }

                string useFilter = Query(request, "land_use");
                string regionTypeFilter = Query(request, "region_type");
                bool? featured = QueryNullableBool(request, "featured");
                long minPrice = QueryLong(request, "min_price", 0, 0, long.MaxValue);
                long maxPrice = QueryLong(request, "max_price", long.MaxValue, 0, long.MaxValue);
                int minArea = QueryInt(request, "min_area", 0, 0, int.MaxValue);
                NexDiscoveryMaturity? maturity = ParseOptionalMaturity(Query(request, "maturity"));

                List<object> results = new List<object>();
                foreach (NexLandListing listing in listings)
                {
                    NexPlaceRecord place = FindPlace(listing);

                    if (listing.PriceMinor < minPrice || listing.PriceMinor > maxPrice || listing.Area < minArea)
                        continue;
                    if (!string.IsNullOrWhiteSpace(useFilter) &&
                        (place == null || !place.LandUse.ToString().Equals(useFilter, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!string.IsNullOrWhiteSpace(regionTypeFilter) &&
                        (place == null || !place.RegionType.Equals(regionTypeFilter, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (featured.HasValue && (place?.Featured ?? false) != featured.Value)
                        continue;
                    if (maturity.HasValue &&
                        place != null &&
                        place.Maturity > maturity.Value)
                        continue;

                    results.Add(LandPayload(listing, place));
                }

                WriteJson(response, new
                {
                    currency = NexLedgerCurrency.Code,
                    count = results.Skip(offset).Take(limit).Count(),
                    listings = results.Skip(offset).Take(limit).ToArray(),
                    correlation_id = Correlation(response)
                });
            }
            catch (Exception e)
            {
                WriteFailure(response, e);
            }
        }

        public void HandleDestinations(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = (request?.UriPath ?? string.Empty).TrimEnd('/');
            string method = request?.HttpMethod ?? string.Empty;

            if (path.Equals("/api/v1/destinations/categories", StringComparison.OrdinalIgnoreCase))
            {
                if (!RequireMethod(request, response, "GET"))
                    return;

                WriteJson(response, new
                {
                    categories = m_Store.GetCategories(),
                    correlation_id = Correlation(response)
                });
                return;
            }

            if (path.Equals("/api/v1/destinations", StringComparison.OrdinalIgnoreCase))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(response, new
                    {
                        destinations = m_Store.ListDestinations(
                            Query(request, "q"),
                            ParseOptionalMaturity(Query(request, "maturity")),
                            NexDiscoveryPublicationState.Approved,
                            Query(request, "category"),
                            Query(request, "collection"),
                            QueryNullableBool(request, "featured"),
                            QueryNullableBool(request, "editor_pick"),
                            Query(request, "sort"),
                            QueryInt(request, "offset", 0, 0, int.MaxValue),
                            QueryInt(request, "limit", 50, 1, 200))
                            .Select(DestinationPayload)
                            .ToArray(),
                        correlation_id = Correlation(response)
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authenticate(request, response, NexScopes.DiscoverySubmit, out NexPrincipal principal, out UserAccount account))
                        return;

                    try
                    {
                        NexDestinationRecord input = ReadBody<NexDestinationRecord>(request);
                        input.State = NexDiscoveryPublicationState.Submitted;
                        if (account != null)
                            input.SubmittedBy = account.PrincipalID.Guid;

                        NexDestinationRecord saved = m_Store.UpsertDestination(input);
                        Audit(principal, "discovery.destination.submitted", "destination:" + saved.DestinationId, response);
                        WriteJson(response, new
                        {
                            destination = DestinationPayload(saved),
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

            string moderationMarker = "/moderation";
            if (path.EndsWith(moderationMarker, StringComparison.OrdinalIgnoreCase))
            {
                string idPath = path.Substring(0, path.Length - moderationMarker.Length);
                if (TryResourceId(idPath, "/api/v1/destinations/", out Guid destinationId))
                {
                    if (!RequireMethod(request, response, "POST"))
                        return;
                    if (!Authenticate(request, response, NexScopes.DiscoveryManage, out NexPrincipal principal, out _))
                        return;

                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(request.InputStream);
                        JsonElement root = document.RootElement;
                        string stateText = StringProperty(root, "state");
                        if (!Enum.TryParse(stateText, true, out NexDiscoveryPublicationState state))
                            throw new ArgumentException("state must be approved or rejected.");

                        NexDestinationRecord saved =
                            m_Store.ModerateDestination(
                                destinationId,
                                state,
                                principal.Subject,
                                StringProperty(root, "note"));

                        Audit(principal, "discovery.destination.moderated", "destination:" + destinationId, response);
                        WriteJson(response, new
                        {
                            destination = DestinationPayload(saved),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }
            }

            string visitMarker = "/visit";
            if (path.EndsWith(visitMarker, StringComparison.OrdinalIgnoreCase))
            {
                string idPath = path.Substring(0, path.Length - visitMarker.Length);
                if (TryResourceId(idPath, "/api/v1/destinations/", out Guid destinationId))
                {
                    if (!RequireMethod(request, response, "POST"))
                        return;

                    try
                    {
                        NexDestinationRecord saved = m_Store.RecordDestinationVisit(destinationId);
                        WriteJson(response, new
                        {
                            popularity = saved.Popularity,
                            teleport_uri = DestinationTeleport(saved),
                            correlation_id = Correlation(response)
                        });
                    }
                    catch (Exception e)
                    {
                        WriteFailure(response, e);
                    }
                    return;
                }
            }

            if (TryResourceId(path, "/api/v1/destinations/", out Guid resourceId))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    NexDestinationRecord destination = m_Store.GetDestination(resourceId);
                    if (destination == null ||
                        destination.State != NexDiscoveryPublicationState.Approved)
                    {
                        NotFound(response, "destination_not_found", "Destination was not found.");
                        return;
                    }

                    WriteJson(response, new
                    {
                        destination = DestinationPayload(destination),
                        place = PlacePayload(m_Store.GetPlace(destination.PlaceId)),
                        events = destination.EventIds
                            .Select(m_Store.GetEvent)
                            .Where(x => x != null && x.State == NexDiscoveryPublicationState.Approved)
                            .ToArray(),
                        correlation_id = Correlation(response)
                    });
                    return;
                }
            }

            NotFound(response, "not_found", "Destination endpoint was not found.");
        }

        private void AddContentHits(
            List<object> hits,
            string query,
            HashSet<string> types,
            NexDiscoveryMaturity? maturity,
            int limit)
        {
            if (Wants(types, "places") || Wants(types, "parcels"))
            {
                foreach (NexPlaceRecord place in
                         m_Store.ListPlaces(
                             query,
                             maturity,
                             NexDiscoveryPublicationState.Approved,
                             0,
                             limit))
                {
                    if (Wants(types, "places"))
                    {
                        hits.Add(new
                        {
                            type = "places",
                            id = place.PlaceId,
                            name = place.Name,
                            description = place.Description,
                            region_name = place.RegionName,
                            maturity = place.Maturity.ToString().ToLowerInvariant(),
                            traffic = place.Traffic,
                            tags = place.Tags,
                            teleport_uri = ResolveTeleport(place)
                        });
                    }

                    if (Wants(types, "parcels") && place.ParcelId != Guid.Empty)
                    {
                        hits.Add(new
                        {
                            type = "parcels",
                            id = place.ParcelId,
                            place_id = place.PlaceId,
                            name = place.Name,
                            region_name = place.RegionName,
                            parcel_local_id = place.ParcelLocalId,
                            details = place.ParcelDetails,
                            teleport_uri = ResolveTeleport(place)
                        });
                    }
                }
            }

            if (Wants(types, "events"))
            {
                foreach (NexEventRecord item in m_Store.ListEvents(
                             query, maturity, NexDiscoveryPublicationState.Approved, 0, limit))
                {
                    hits.Add(new
                    {
                        type = "events",
                        id = item.EventId,
                        name = item.Name,
                        description = item.Description,
                        starts_at = item.StartsAt,
                        ends_at = item.EndsAt,
                        place_id = item.PlaceId,
                        maturity = item.Maturity.ToString().ToLowerInvariant()
                    });
                }
            }

            if (Wants(types, "classifieds"))
            {
                foreach (NexClassifiedRecord item in m_Store.ListClassifieds(
                             query, maturity, NexDiscoveryPublicationState.Approved, 0, limit))
                {
                    hits.Add(new
                    {
                        type = "classifieds",
                        id = item.ClassifiedId,
                        name = item.Name,
                        description = item.Description,
                        category = item.Category,
                        place_id = item.PlaceId
                    });
                }
            }

            if (Wants(types, "destinations"))
            {
                foreach (NexDestinationRecord item in m_Store.ListDestinations(
                             query, maturity, NexDiscoveryPublicationState.Approved,
                             string.Empty, string.Empty, null, null, string.Empty, 0, limit))
                {
                    hits.Add(new
                    {
                        type = "destinations",
                        id = item.DestinationId,
                        name = item.Name,
                        description = item.Description,
                        category = item.Category,
                        subcategory = item.Subcategory,
                        featured = item.Featured,
                        editor_pick = item.EditorPick,
                        popularity = item.Popularity,
                        teleport_uri = DestinationTeleport(item)
                    });
                }
            }
        }

        private void AddLandHits(
            List<object> hits,
            string query,
            HashSet<string> types,
            NexDiscoveryMaturity? maturity,
            int limit)
        {
            bool sale = Wants(types, "land_for_sale");
            bool rent = Wants(types, "land_for_rent");
            if (!sale && !rent)
                return;

            NexEconomyService economy = m_Economy();
            if (economy == null)
                return;

            foreach (NexLandListing listing in
                     economy.SearchLandListings(query, null, 0, limit * 2)
                         .Where(x => x.Active))
            {
                if (listing.ListingType == NexLandListingType.Sale && !sale)
                    continue;
                if (listing.ListingType == NexLandListingType.Rental && !rent)
                    continue;

                NexPlaceRecord place = FindPlace(listing);
                if (maturity.HasValue && place != null && place.Maturity > maturity.Value)
                    continue;

                hits.Add(LandPayload(listing, place));
            }
        }

        private object LandPayload(NexLandListing listing, NexPlaceRecord place) =>
            new
            {
                type = listing.ListingType == NexLandListingType.Sale
                    ? "land_for_sale"
                    : "land_for_rent",
                listing_id = listing.ListingId,
                region_id = listing.RegionId,
                region_name = listing.RegionName,
                parcel_id = listing.ParcelId,
                parcel_local_id = listing.ParcelLocalId,
                parcel_name = listing.ParcelName,
                seller_account_id = listing.SellerAccountId,
                estate_id = listing.EstateId,
                area = listing.Area,
                price = listing.PriceMinor,
                currency = NexLedgerCurrency.Code,
                rental_period_days = listing.RentalPeriodDays,
                active = listing.Active,
                created_at = listing.CreatedAt,
                place_id = place?.PlaceId ?? Guid.Empty,
                maturity = place?.Maturity.ToString().ToLowerInvariant() ?? string.Empty,
                land_use = place?.LandUse.ToString().ToLowerInvariant() ?? string.Empty,
                region_type = place?.RegionType ?? string.Empty,
                featured = place?.Featured ?? false,
                map_location = place == null
                    ? null
                    : new { x = place.X, y = place.Y, z = place.Z },
                teleport_uri = place == null
                    ? BuildTeleport(listing.RegionName, 128, 128, 25)
                    : ResolveTeleport(place)
            };

        private NexPlaceRecord FindPlace(NexLandListing listing) =>
            m_Store.ListPlaces(
                    string.Empty,
                    null,
                    null,
                    0,
                    500)
                .FirstOrDefault(x =>
                    (x.ParcelId != Guid.Empty && x.ParcelId == listing.ParcelId) ||
                    (x.RegionId == listing.RegionId &&
                     x.ParcelLocalId > 0 &&
                     x.ParcelLocalId == listing.ParcelLocalId));

        private object PlacePayload(NexPlaceRecord place)
        {
            if (place == null)
                return null;

            return new
            {
                place_id = place.PlaceId,
                region_id = place.RegionId,
                region_name = place.RegionName,
                parcel_id = place.ParcelId,
                parcel_local_id = place.ParcelLocalId,
                name = place.Name,
                description = place.Description,
                maturity = place.Maturity.ToString().ToLowerInvariant(),
                images = place.Images,
                owner_id = place.OwnerId,
                coordinates = new { x = place.X, y = place.Y, z = place.Z },
                teleport_uri = ResolveTeleport(place),
                parcel_details = place.ParcelDetails,
                traffic = place.Traffic,
                tags = place.Tags,
                events = place.EventIds,
                related_destinations = place.RelatedDestinationIds,
                land_use = place.LandUse.ToString().ToLowerInvariant(),
                region_type = place.RegionType,
                featured = place.Featured,
                state = place.State.ToString().ToLowerInvariant(),
                created_at = place.CreatedAt,
                updated_at = place.UpdatedAt
            };
        }

        private object DestinationPayload(NexDestinationRecord destination)
        {
            if (destination == null)
                return null;

            return new
            {
                destination_id = destination.DestinationId,
                place_id = destination.PlaceId,
                submitted_by = destination.SubmittedBy,
                name = destination.Name,
                description = destination.Description,
                category = destination.Category,
                subcategory = destination.Subcategory,
                collection = destination.Collection,
                image = destination.Image,
                maturity = destination.Maturity.ToString().ToLowerInvariant(),
                featured = destination.Featured,
                editor_pick = destination.EditorPick,
                popularity = destination.Popularity,
                tags = destination.Tags,
                events = destination.EventIds,
                state = destination.State.ToString().ToLowerInvariant(),
                moderation_note = destination.ModerationNote,
                teleport_uri = DestinationTeleport(destination),
                created_at = destination.CreatedAt,
                updated_at = destination.UpdatedAt
            };
        }

        private string DestinationTeleport(NexDestinationRecord destination)
        {
            NexPlaceRecord place = m_Store.GetPlace(destination.PlaceId);
            return place == null ? string.Empty : ResolveTeleport(place);
        }

        private string ResolveTeleport(NexPlaceRecord place) =>
            !string.IsNullOrWhiteSpace(place.TeleportUri)
                ? place.TeleportUri
                : BuildTeleport(place.RegionName, place.X, place.Y, place.Z);

        private string BuildTeleport(string regionName, int x, int y, int z)
        {
            if (string.IsNullOrWhiteSpace(m_TeleportBaseUri) ||
                string.IsNullOrWhiteSpace(regionName))
            {
                return string.Empty;
            }

            return m_TeleportBaseUri +
                "/" + Uri.EscapeDataString(regionName.Trim()) +
                "/" + x +
                "/" + y +
                "/" + z;
        }

        private static NexDiscoveryMaturity RegionMaturity(GridRegion region)
        {
            int raw = region?.Maturity ?? 0;
            if (raw >= 2)
                return NexDiscoveryMaturity.Adult;
            if (raw == 1)
                return NexDiscoveryMaturity.Moderate;
            return NexDiscoveryMaturity.General;
        }

        private static bool AllowsMaturity(
            NexDiscoveryMaturity? maximum,
            NexDiscoveryMaturity actual) =>
            !maximum.HasValue || actual <= maximum.Value;

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

        private void Audit(
            NexPrincipal principal,
            string action,
            string resource,
            IOSHttpResponse response)
        {
            m_Audit.Record(new NexAuditEvent(
                principal?.Subject ?? "unknown",
                action,
                resource,
                Correlation(response),
                new Dictionary<string, string>()));
        }

        private static T ReadBody<T>(IOSHttpRequest request)
        {
            T value =
                JsonSerializer.Deserialize<T>(
                    request.InputStream,
                    s_Json);

            if (value == null)
                throw new ArgumentException("A valid JSON body is required.");

            return value;
        }

        private static string StringProperty(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }
            return (value.GetString() ?? string.Empty).Trim();
        }

        private static string Query(IOSHttpRequest request, string key) =>
            (request?.QueryString?[key] ?? string.Empty).Trim();

        private static int QueryInt(
            IOSHttpRequest request,
            string key,
            int defaultValue,
            int min,
            int max)
        {
            string raw = Query(request, key);
            if (raw.Length == 0)
                return defaultValue;
            if (!int.TryParse(raw, out int value) || value < min || value > max)
                throw new ArgumentException(key + " is outside the allowed range.");
            return value;
        }

        private static long QueryLong(
            IOSHttpRequest request,
            string key,
            long defaultValue,
            long min,
            long max)
        {
            string raw = Query(request, key);
            if (raw.Length == 0)
                return defaultValue;
            if (!long.TryParse(raw, out long value) || value < min || value > max)
                throw new ArgumentException(key + " is outside the allowed range.");
            return value;
        }

        private static Guid QueryGuid(IOSHttpRequest request, string key)
        {
            if (!Guid.TryParse(Query(request, key), out Guid value) ||
                value == Guid.Empty)
            {
                throw new ArgumentException(key + " must be a non-zero UUID.");
            }
            return value;
        }

        private static bool? QueryNullableBool(IOSHttpRequest request, string key)
        {
            string raw = Query(request, key);
            if (raw.Length == 0)
                return null;
            if (!bool.TryParse(raw, out bool value))
                throw new ArgumentException(key + " must be boolean.");
            return value;
        }

        private static NexDiscoveryMaturity? ParseOptionalMaturity(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (!Enum.TryParse(raw, true, out NexDiscoveryMaturity value))
                throw new ArgumentException("maturity must be general, moderate or adult.");
            return value;
        }

        private static NexLandListingType? ParseLandListingType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (raw.Equals("sale", StringComparison.OrdinalIgnoreCase))
                return NexLandListingType.Sale;
            if (raw.Equals("rental", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("rent", StringComparison.OrdinalIgnoreCase))
                return NexLandListingType.Rental;

            throw new ArgumentException("type must be sale, rental or all.");
        }

        private static HashSet<string> ParseTypes(string raw)
        {
            return new HashSet<string>(
                (raw ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool Wants(HashSet<string> types, string value) =>
            types == null ||
            types.Count == 0 ||
            types.Contains("all") ||
            types.Contains(value);

        private static bool TryResourceId(
            string path,
            string prefix,
            out Guid id)
        {
            id = Guid.Empty;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string tail = path.Substring(prefix.Length);
            return !tail.Contains('/') &&
                   Guid.TryParse(tail, out id) &&
                   id != Guid.Empty;
        }

        private static bool RequireMethod(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string expected)
        {
            if (string.Equals(request?.HttpMethod, expected, StringComparison.OrdinalIgnoreCase))
                return true;

            MethodNotAllowed(response, expected);
            return false;
        }

        private static string Correlation(IOSHttpResponse response) =>
            NexApiRequestContext.Ensure(response);

        private static void WriteFailure(IOSHttpResponse response, Exception exception)
        {
            if (exception is KeyNotFoundException)
            {
                NotFound(response, "discovery_not_found", exception.Message);
                return;
            }

            if (exception is UnauthorizedAccessException)
            {
                WriteError(response, HttpStatusCode.Forbidden, "discovery_forbidden", exception.Message);
                return;
            }

            if (exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                WriteError(response, HttpStatusCode.BadRequest, "discovery_validation_failed", exception.Message);
                return;
            }

            WriteError(response, HttpStatusCode.InternalServerError, "discovery_internal_error", "The discovery operation failed.");
        }

        private static void MethodNotAllowed(IOSHttpResponse response, string expected) =>
            WriteError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", expected + " is required.");

        private static void NotFound(
            IOSHttpResponse response,
            string error,
            string message) =>
            WriteError(response, HttpStatusCode.NotFound, error, message);

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
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(payload, s_Json);
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }
    }
}
