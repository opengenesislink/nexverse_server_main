// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexVerse.Core.Discovery
{
    public enum NexDiscoveryMaturity
    {
        General = 0,
        Moderate = 1,
        Adult = 2
    }

    public enum NexDiscoveryPublicationState
    {
        Draft = 0,
        Submitted = 1,
        Approved = 2,
        Rejected = 3
    }

    public enum NexLandUse
    {
        Other = 0,
        Residential = 1,
        Commercial = 2,
        Mixed = 3
    }

    public sealed class NexPlaceRecord
    {
        public Guid PlaceId { get; set; }
        public Guid RegionId { get; set; }
        public string RegionName { get; set; } = string.Empty;
        public Guid ParcelId { get; set; }
        public int ParcelLocalId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public NexDiscoveryMaturity Maturity { get; set; } = NexDiscoveryMaturity.General;
        public List<string> Images { get; set; } = new List<string>();
        public Guid OwnerId { get; set; }
        public int X { get; set; } = 128;
        public int Y { get; set; } = 128;
        public int Z { get; set; } = 25;
        public string TeleportUri { get; set; } = string.Empty;
        public string ParcelDetails { get; set; } = string.Empty;
        public double Traffic { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<Guid> EventIds { get; set; } = new List<Guid>();
        public List<Guid> RelatedDestinationIds { get; set; } = new List<Guid>();
        public NexLandUse LandUse { get; set; } = NexLandUse.Other;
        public string RegionType { get; set; } = string.Empty;
        public bool Featured { get; set; }
        public NexDiscoveryPublicationState State { get; set; } = NexDiscoveryPublicationState.Draft;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexEventRecord
    {
        public Guid EventId { get; set; }
        public Guid PlaceId { get; set; }
        public Guid OrganizerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public NexDiscoveryMaturity Maturity { get; set; } = NexDiscoveryMaturity.General;
        public DateTimeOffset StartsAt { get; set; }
        public DateTimeOffset EndsAt { get; set; }
        public long AdmissionPriceMinor { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public NexDiscoveryPublicationState State { get; set; } = NexDiscoveryPublicationState.Draft;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexClassifiedRecord
    {
        public Guid ClassifiedId { get; set; }
        public Guid OwnerId { get; set; }
        public Guid PlaceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public NexDiscoveryMaturity Maturity { get; set; } = NexDiscoveryMaturity.General;
        public List<string> Tags { get; set; } = new List<string>();
        public NexDiscoveryPublicationState State { get; set; } = NexDiscoveryPublicationState.Draft;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexDestinationRecord
    {
        public Guid DestinationId { get; set; }
        public Guid PlaceId { get; set; }
        public Guid SubmittedBy { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Subcategory { get; set; } = string.Empty;
        public string Collection { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public NexDiscoveryMaturity Maturity { get; set; } = NexDiscoveryMaturity.General;
        public bool Featured { get; set; }
        public bool EditorPick { get; set; }
        public int Popularity { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<Guid> EventIds { get; set; } = new List<Guid>();
        public NexDiscoveryPublicationState State { get; set; } = NexDiscoveryPublicationState.Draft;
        public string ModerationNote { get; set; } = string.Empty;
        public string ModeratedBy { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexDiscoverySnapshot
    {
        public int SchemaVersion { get; set; } = NexDiscoveryStore.CurrentSchemaVersion;
        public List<NexPlaceRecord> Places { get; set; } = new List<NexPlaceRecord>();
        public List<NexEventRecord> Events { get; set; } = new List<NexEventRecord>();
        public List<NexClassifiedRecord> Classifieds { get; set; } = new List<NexClassifiedRecord>();
        public List<NexDestinationRecord> Destinations { get; set; } = new List<NexDestinationRecord>();
    }

    public sealed class NexDiscoveryStore
    {
        public const int CurrentSchemaVersion = 1;

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private readonly JsonSerializerOptions m_Json;
        private NexDiscoverySnapshot m_State;

        public NexDiscoveryStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Discovery store path is required.", nameof(path));

            m_Path = Path.GetFullPath(path);
            m_Json = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };
            m_Json.Converters.Add(new JsonStringEnumConverter());
            m_State = Load();
        }

        public NexPlaceRecord GetPlace(Guid id)
        {
            lock (m_Sync)
                return Clone(m_State.Places.FirstOrDefault(x => x.PlaceId == id));
        }

        public IReadOnlyList<NexPlaceRecord> ListPlaces(
            string query,
            NexDiscoveryMaturity? maximumMaturity,
            NexDiscoveryPublicationState? state,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);
            string q = NormalizeQuery(query);

            lock (m_Sync)
            {
                IEnumerable<NexPlaceRecord> source = m_State.Places;

                if (state.HasValue)
                    source = source.Where(x => x.State == state.Value);
                if (maximumMaturity.HasValue)
                    source = source.Where(x => x.Maturity <= maximumMaturity.Value);
                if (q.Length > 0)
                    source = source.Where(x => Matches(q, x.Name, x.RegionName, x.Description, x.ParcelDetails, string.Join(" ", x.Tags)));

                return source
                    .OrderByDescending(x => x.Featured)
                    .ThenByDescending(x => x.Traffic)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexPlaceRecord UpsertPlace(NexPlaceRecord input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            NexPlaceRecord record = NormalizePlace(input);

            lock (m_Sync)
            {
                NexPlaceRecord existing = m_State.Places.FirstOrDefault(x => x.PlaceId == record.PlaceId);
                if (existing != null)
                {
                    record.CreatedAt = existing.CreatedAt;
                    m_State.Places.Remove(existing);
                }

                record.UpdatedAt = DateTimeOffset.UtcNow;
                m_State.Places.Add(record);
                Save();
                return Clone(record);
            }
        }

        public bool DeletePlace(Guid id)
        {
            lock (m_Sync)
            {
                bool removed = m_State.Places.RemoveAll(x => x.PlaceId == id) > 0;
                if (removed)
                {
                    m_State.Destinations.RemoveAll(x => x.PlaceId == id);
                    m_State.Events.RemoveAll(x => x.PlaceId == id);
                    m_State.Classifieds.RemoveAll(x => x.PlaceId == id);
                    Save();
                }
                return removed;
            }
        }

        public NexEventRecord GetEvent(Guid id)
        {
            lock (m_Sync)
                return Clone(m_State.Events.FirstOrDefault(x => x.EventId == id));
        }

        public IReadOnlyList<NexEventRecord> ListEvents(
            string query,
            NexDiscoveryMaturity? maximumMaturity,
            NexDiscoveryPublicationState? state,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);
            string q = NormalizeQuery(query);

            lock (m_Sync)
            {
                IEnumerable<NexEventRecord> source = m_State.Events;

                if (state.HasValue)
                    source = source.Where(x => x.State == state.Value);
                if (maximumMaturity.HasValue)
                    source = source.Where(x => x.Maturity <= maximumMaturity.Value);
                if (q.Length > 0)
                    source = source.Where(x => Matches(q, x.Name, x.Description, string.Join(" ", x.Tags)));

                return source
                    .OrderBy(x => x.StartsAt)
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexEventRecord UpsertEvent(NexEventRecord input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (input.EventId == Guid.Empty)
                input.EventId = Guid.NewGuid();
            RequireName(input.Name, nameof(input.Name));
            if (input.PlaceId == Guid.Empty)
                throw new ArgumentException("Event place is required.");
            if (input.EndsAt <= input.StartsAt)
                throw new ArgumentException("Event end must be after start.");
            if (input.AdmissionPriceMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(input.AdmissionPriceMinor));

            NexEventRecord record = Clone(input);
            record.Name = record.Name.Trim();
            record.Description = TrimMax(record.Description, 8192);
            record.Tags = NormalizeTags(record.Tags);
            record.UpdatedAt = DateTimeOffset.UtcNow;

            lock (m_Sync)
            {
                if (!m_State.Places.Any(x => x.PlaceId == record.PlaceId))
                    throw new InvalidOperationException("Event place was not found.");

                NexEventRecord existing = m_State.Events.FirstOrDefault(x => x.EventId == record.EventId);
                if (existing != null)
                {
                    record.CreatedAt = existing.CreatedAt;
                    m_State.Events.Remove(existing);
                }

                m_State.Events.Add(record);
                Save();
                return Clone(record);
            }
        }

        public bool DeleteEvent(Guid id)
        {
            lock (m_Sync)
            {
                bool removed = m_State.Events.RemoveAll(x => x.EventId == id) > 0;
                foreach (NexPlaceRecord place in m_State.Places)
                    place.EventIds.RemoveAll(x => x == id);
                foreach (NexDestinationRecord destination in m_State.Destinations)
                    destination.EventIds.RemoveAll(x => x == id);
                if (removed)
                    Save();
                return removed;
            }
        }

        public NexClassifiedRecord GetClassified(Guid id)
        {
            lock (m_Sync)
                return Clone(m_State.Classifieds.FirstOrDefault(x => x.ClassifiedId == id));
        }

        public IReadOnlyList<NexClassifiedRecord> ListClassifieds(
            string query,
            NexDiscoveryMaturity? maximumMaturity,
            NexDiscoveryPublicationState? state,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);
            string q = NormalizeQuery(query);

            lock (m_Sync)
            {
                IEnumerable<NexClassifiedRecord> source = m_State.Classifieds;
                if (state.HasValue)
                    source = source.Where(x => x.State == state.Value);
                if (maximumMaturity.HasValue)
                    source = source.Where(x => x.Maturity <= maximumMaturity.Value);
                if (q.Length > 0)
                    source = source.Where(x => Matches(q, x.Name, x.Description, x.Category, string.Join(" ", x.Tags)));

                return source
                    .OrderByDescending(x => x.UpdatedAt)
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexClassifiedRecord UpsertClassified(NexClassifiedRecord input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (input.ClassifiedId == Guid.Empty)
                input.ClassifiedId = Guid.NewGuid();
            RequireName(input.Name, nameof(input.Name));

            NexClassifiedRecord record = Clone(input);
            record.Name = record.Name.Trim();
            record.Description = TrimMax(record.Description, 8192);
            record.Category = TrimMax(record.Category, 128);
            record.Tags = NormalizeTags(record.Tags);
            record.UpdatedAt = DateTimeOffset.UtcNow;

            lock (m_Sync)
            {
                if (record.PlaceId != Guid.Empty &&
                    !m_State.Places.Any(x => x.PlaceId == record.PlaceId))
                {
                    throw new InvalidOperationException("Classified place was not found.");
                }

                NexClassifiedRecord existing =
                    m_State.Classifieds.FirstOrDefault(x => x.ClassifiedId == record.ClassifiedId);

                if (existing != null)
                {
                    record.CreatedAt = existing.CreatedAt;
                    m_State.Classifieds.Remove(existing);
                }

                m_State.Classifieds.Add(record);
                Save();
                return Clone(record);
            }
        }

        public bool DeleteClassified(Guid id)
        {
            lock (m_Sync)
            {
                bool removed = m_State.Classifieds.RemoveAll(x => x.ClassifiedId == id) > 0;
                if (removed)
                    Save();
                return removed;
            }
        }

        public NexDestinationRecord GetDestination(Guid id)
        {
            lock (m_Sync)
                return Clone(m_State.Destinations.FirstOrDefault(x => x.DestinationId == id));
        }

        public IReadOnlyList<NexDestinationRecord> ListDestinations(
            string query,
            NexDiscoveryMaturity? maximumMaturity,
            NexDiscoveryPublicationState? state,
            string category,
            string collection,
            bool? featured,
            bool? editorPick,
            string sort,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);
            string q = NormalizeQuery(query);
            string categoryKey = NormalizeQuery(category);
            string collectionKey = NormalizeQuery(collection);

            lock (m_Sync)
            {
                IEnumerable<NexDestinationRecord> source = m_State.Destinations;

                if (state.HasValue)
                    source = source.Where(x => x.State == state.Value);
                if (maximumMaturity.HasValue)
                    source = source.Where(x => x.Maturity <= maximumMaturity.Value);
                if (featured.HasValue)
                    source = source.Where(x => x.Featured == featured.Value);
                if (editorPick.HasValue)
                    source = source.Where(x => x.EditorPick == editorPick.Value);
                if (categoryKey.Length > 0)
                    source = source.Where(x => NormalizeQuery(x.Category) == categoryKey);
                if (collectionKey.Length > 0)
                    source = source.Where(x => NormalizeQuery(x.Collection) == collectionKey);
                if (q.Length > 0)
                    source = source.Where(x => Matches(q, x.Name, x.Description, x.Category, x.Subcategory, x.Collection, string.Join(" ", x.Tags)));

                source = (sort ?? string.Empty).Trim().ToLowerInvariant() switch
                {
                    "recent" => source.OrderByDescending(x => x.CreatedAt),
                    "popular" => source.OrderByDescending(x => x.Popularity).ThenByDescending(x => x.UpdatedAt),
                    "hot" => source.OrderByDescending(x => x.Popularity).ThenByDescending(x => x.CreatedAt),
                    _ => source.OrderByDescending(x => x.EditorPick).ThenByDescending(x => x.Featured).ThenByDescending(x => x.Popularity)
                };

                return source
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexDestinationRecord UpsertDestination(NexDestinationRecord input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (input.DestinationId == Guid.Empty)
                input.DestinationId = Guid.NewGuid();
            if (input.PlaceId == Guid.Empty)
                throw new ArgumentException("Destination place is required.");
            RequireName(input.Name, nameof(input.Name));

            NexDestinationRecord record = Clone(input);
            record.Name = record.Name.Trim();
            record.Description = TrimMax(record.Description, 8192);
            record.Category = TrimMax(record.Category, 128);
            record.Subcategory = TrimMax(record.Subcategory, 128);
            record.Collection = TrimMax(record.Collection, 128);
            record.Tags = NormalizeTags(record.Tags);
            record.UpdatedAt = DateTimeOffset.UtcNow;

            lock (m_Sync)
            {
                if (!m_State.Places.Any(x => x.PlaceId == record.PlaceId))
                    throw new InvalidOperationException("Destination place was not found.");

                NexDestinationRecord existing =
                    m_State.Destinations.FirstOrDefault(x => x.DestinationId == record.DestinationId);

                if (existing != null)
                {
                    record.CreatedAt = existing.CreatedAt;
                    record.Popularity = Math.Max(existing.Popularity, record.Popularity);
                    record.ModeratedBy = existing.ModeratedBy;
                    record.ModerationNote = existing.ModerationNote;
                    m_State.Destinations.Remove(existing);
                }

                m_State.Destinations.Add(record);
                Save();
                return Clone(record);
            }
        }

        public NexDestinationRecord ModerateDestination(
            Guid destinationId,
            NexDiscoveryPublicationState state,
            string moderator,
            string note)
        {
            if (state != NexDiscoveryPublicationState.Approved &&
                state != NexDiscoveryPublicationState.Rejected)
            {
                throw new ArgumentException("Moderation state must be Approved or Rejected.");
            }

            lock (m_Sync)
            {
                NexDestinationRecord record =
                    m_State.Destinations.FirstOrDefault(x => x.DestinationId == destinationId) ??
                    throw new KeyNotFoundException("Destination was not found.");

                record.State = state;
                record.ModeratedBy = (moderator ?? string.Empty).Trim();
                record.ModerationNote = TrimMax(note, 2048);
                record.UpdatedAt = DateTimeOffset.UtcNow;
                Save();
                return Clone(record);
            }
        }

        public NexDestinationRecord RecordDestinationVisit(Guid destinationId)
        {
            lock (m_Sync)
            {
                NexDestinationRecord record =
                    m_State.Destinations.FirstOrDefault(x => x.DestinationId == destinationId) ??
                    throw new KeyNotFoundException("Destination was not found.");

                if (record.State != NexDiscoveryPublicationState.Approved)
                    throw new InvalidOperationException("Only approved destinations can receive visits.");

                record.Popularity = checked(record.Popularity + 1);
                record.UpdatedAt = DateTimeOffset.UtcNow;
                Save();
                return Clone(record);
            }
        }

        public IReadOnlyList<object> GetCategories()
        {
            lock (m_Sync)
            {
                return m_State.Destinations
                    .Where(x => x.State == NexDiscoveryPublicationState.Approved)
                    .GroupBy(x => x.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (object)new
                    {
                        category = x.Key,
                        subcategories = x
                            .Select(y => y.Subcategory)
                            .Where(y => !string.IsNullOrWhiteSpace(y))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(y => y, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        count = x.Count()
                    })
                    .ToArray();
            }
        }

        private NexDiscoverySnapshot Load()
        {
            if (!File.Exists(m_Path))
                return new NexDiscoverySnapshot();

            NexDiscoverySnapshot state =
                JsonSerializer.Deserialize<NexDiscoverySnapshot>(
                    File.ReadAllText(m_Path),
                    m_Json) ??
                new NexDiscoverySnapshot();

            if (state.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported NexDiscovery schema version.");

            state.Places ??= new List<NexPlaceRecord>();
            state.Events ??= new List<NexEventRecord>();
            state.Classifieds ??= new List<NexClassifiedRecord>();
            state.Destinations ??= new List<NexDestinationRecord>();
            return state;
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temp = m_Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(m_State, m_Json));
            File.Move(temp, m_Path, true);
        }

        private NexPlaceRecord NormalizePlace(NexPlaceRecord input)
        {
            NexPlaceRecord record = Clone(input);
            if (record.PlaceId == Guid.Empty)
                record.PlaceId = Guid.NewGuid();
            if (record.RegionId == Guid.Empty)
                throw new ArgumentException("Place region is required.");
            RequireName(record.Name, nameof(record.Name));

            record.Name = record.Name.Trim();
            record.RegionName = TrimMax(record.RegionName, 255);
            record.Description = TrimMax(record.Description, 8192);
            record.ParcelDetails = TrimMax(record.ParcelDetails, 8192);
            record.RegionType = TrimMax(record.RegionType, 128);
            record.TeleportUri = TrimMax(record.TeleportUri, 2048);
            record.Images = NormalizeStrings(record.Images, 16, 2048);
            record.Tags = NormalizeTags(record.Tags);
            record.EventIds ??= new List<Guid>();
            record.RelatedDestinationIds ??= new List<Guid>();

            if (record.X < 0 || record.Y < 0 || record.Z < 0)
                throw new ArgumentOutOfRangeException("Place coordinates must be non-negative.");
            if (record.Traffic < 0 || double.IsNaN(record.Traffic) || double.IsInfinity(record.Traffic))
                throw new ArgumentOutOfRangeException(nameof(record.Traffic));

            return record;
        }

        private static List<string> NormalizeTags(IEnumerable<string> values) =>
            NormalizeStrings(values, 32, 64)
                .Select(x => x.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static List<string> NormalizeStrings(
            IEnumerable<string> values,
            int maximumCount,
            int maximumLength)
        {
            return (values ?? Array.Empty<string>())
                .Select(x => TrimMax(x, maximumLength))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maximumCount)
                .ToList();
        }

        private static string TrimMax(string value, int max)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Length <= max
                ? normalized
                : normalized.Substring(0, max);
        }

        private static void RequireName(string value, string parameter)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > 255)
                throw new ArgumentException("A name between 1 and 255 characters is required.", parameter);
        }

        private static string NormalizeQuery(string value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant();

        private static bool Matches(string query, params string[] values) =>
            values.Any(x => NormalizeQuery(x).Contains(query, StringComparison.Ordinal));

        private static void ValidatePage(int offset, int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 500)
                throw new ArgumentOutOfRangeException(nameof(limit));
        }

        private T Clone<T>(T value)
        {
            if (value == null)
                return default;

            return JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(value, m_Json),
                m_Json);
        }
    }
}
