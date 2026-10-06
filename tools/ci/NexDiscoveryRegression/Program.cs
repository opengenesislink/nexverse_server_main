// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Linq;
using NexVerse.Core.Discovery;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Main()
    {
        string root =
            Path.Combine(
                Path.GetTempPath(),
                "ogl-discovery-" +
                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string path =
            Path.Combine(
                root,
                "discovery.json");

        NexDiscoveryStore store =
            new NexDiscoveryStore(path);

        Guid regionId = Guid.NewGuid();
        Guid parcelId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();

        NexPlaceRecord place =
            store.UpsertPlace(
                new NexPlaceRecord
                {
                    RegionId = regionId,
                    RegionName = "NexVerse Landing",
                    ParcelId = parcelId,
                    ParcelLocalId = 1,
                    OwnerId = ownerId,
                    Name = "NexVerse Landing Plaza",
                    Description = "Central destination for the grid",
                    Maturity = NexDiscoveryMaturity.General,
                    Images = { "https://example.invalid/place.jpg" },
                    X = 171,
                    Y = 126,
                    Z = 25,
                    Traffic = 123.5,
                    Tags = { "city", "landing", "welcome" },
                    LandUse = NexLandUse.Commercial,
                    RegionType = "city",
                    Featured = true,
                    State = NexDiscoveryPublicationState.Approved
                });

        Require(
            place.PlaceId != Guid.Empty,
            "place ID was not assigned");

        Require(
            store.ListPlaces(
                "landing",
                NexDiscoveryMaturity.General,
                NexDiscoveryPublicationState.Approved,
                0,
                10).Count == 1,
            "place search failed");

        NexEventRecord eventRecord =
            store.UpsertEvent(
                new NexEventRecord
                {
                    PlaceId = place.PlaceId,
                    OrganizerId = ownerId,
                    Name = "Welcome Night",
                    Description = "Opening event",
                    StartsAt = DateTimeOffset.UtcNow.AddHours(1),
                    EndsAt = DateTimeOffset.UtcNow.AddHours(3),
                    Tags = { "party", "welcome" },
                    State = NexDiscoveryPublicationState.Approved
                });

        NexClassifiedRecord classified =
            store.UpsertClassified(
                new NexClassifiedRecord
                {
                    OwnerId = ownerId,
                    PlaceId = place.PlaceId,
                    Name = "Shop Space",
                    Description = "Commercial parcel offer",
                    Category = "land",
                    Tags = { "shop", "commercial" },
                    State = NexDiscoveryPublicationState.Approved
                });

        NexDestinationRecord destination =
            store.UpsertDestination(
                new NexDestinationRecord
                {
                    PlaceId = place.PlaceId,
                    SubmittedBy = ownerId,
                    Name = "NexVerse City Start",
                    Description = "Editor destination",
                    Category = "city",
                    Subcategory = "welcome",
                    Collection = "stadt-nexverse",
                    Featured = true,
                    EditorPick = true,
                    Tags = { "city", "start" },
                    EventIds = { eventRecord.EventId },
                    State = NexDiscoveryPublicationState.Submitted
                });

        Require(
            store.ListDestinations(
                string.Empty,
                null,
                NexDiscoveryPublicationState.Approved,
                string.Empty,
                string.Empty,
                null,
                null,
                string.Empty,
                0,
                10).Count == 0,
            "submitted destination leaked into public results");

        destination =
            store.ModerateDestination(
                destination.DestinationId,
                NexDiscoveryPublicationState.Approved,
                "ci-admin",
                "approved");

        Require(
            destination.State ==
                NexDiscoveryPublicationState.Approved,
            "destination moderation failed");

        store.RecordDestinationVisit(
            destination.DestinationId);
        store.RecordDestinationVisit(
            destination.DestinationId);

        Require(
            store.GetDestination(
                destination.DestinationId).Popularity == 2,
            "destination popularity tracking failed");

        Require(
            store.GetCategories().Count == 1,
            "destination category projection failed");

        NexDiscoveryStore reopened =
            new NexDiscoveryStore(path);

        Require(
            reopened.GetPlace(place.PlaceId) != null &&
            reopened.GetEvent(eventRecord.EventId) != null &&
            reopened.GetClassified(classified.ClassifiedId) != null &&
            reopened.GetDestination(destination.DestinationId) != null,
            "discovery persistence/reopen failed");

        Require(
            reopened.ListDestinations(
                "nexverse",
                NexDiscoveryMaturity.General,
                NexDiscoveryPublicationState.Approved,
                "city",
                "stadt-nexverse",
                true,
                true,
                "popular",
                0,
                10).Count == 1,
            "destination filters failed");

        Require(
            reopened.DeletePlace(place.PlaceId),
            "place deletion failed");

        Require(
            reopened.GetPlace(place.PlaceId) == null &&
            reopened.GetEvent(eventRecord.EventId) == null &&
            reopened.GetClassified(classified.ClassifiedId) == null &&
            reopened.GetDestination(destination.DestinationId) == null,
            "place cascade cleanup failed");

        Directory.Delete(root, true);

        Console.WriteLine(
            "OpenGenesisLINK NexDiscovery persistence, moderation and cascade regression: OK");
    }
}
