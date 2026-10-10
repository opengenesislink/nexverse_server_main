// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NexVerse.Core.Experiences
{
    public enum NexExperienceMaturity
    {
        General = 0,
        Moderate = 1,
        Adult = 2
    }

    public enum NexExperiencePermissionStatus
    {
        None = 0,
        Allowed = 1,
        Blocked = 2
    }

    public sealed class NexExperience
    {
        public Guid ExperienceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }
        public Guid GroupId { get; set; }
        public NexExperienceMaturity Maturity { get; set; } = NexExperienceMaturity.General;
        public bool Enabled { get; set; } = true;
        public HashSet<Guid> Admins { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> Contributors { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> AllowedResidents { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> BlockedResidents { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> AllowedEstates { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> BlockedEstates { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> AllowedParcels { get; set; } = new HashSet<Guid>();
        public HashSet<Guid> BlockedParcels { get; set; } = new HashSet<Guid>();
        public Dictionary<string, string> KeyValues { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class NexExperienceLogEntry
    {
        public Guid LogId { get; set; } = Guid.NewGuid();
        public Guid ExperienceId { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public string Actor { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public sealed class NexExperienceSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public List<NexExperience> Experiences { get; set; } = new List<NexExperience>();
        public Dictionary<Guid, Guid> ScriptBindings { get; set; } = new Dictionary<Guid, Guid>();
        public List<NexExperienceLogEntry> Logs { get; set; } = new List<NexExperienceLogEntry>();
    }

    public sealed class NexExperienceStore
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaxKeyLength = 1011;
        public const int MaxValueLength = 4095;
        public const int MaxKeysPerExperience = 65536;
        public const long MaxStoreBytes = 128L * 1024L * 1024L;
        public const int MaxLogs = 10000;

        private readonly object m_Sync = new object();
        private readonly string m_Path;
        private readonly JsonSerializerOptions m_Json =
            new JsonSerializerOptions { WriteIndented = true };
        private NexExperienceSnapshot m_State;

        public NexExperienceStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Experience store path is required.", nameof(path));

            m_Path = Path.GetFullPath(path);
            m_State = Load();
        }

        public NexExperience Create(
            Guid ownerId,
            Guid groupId,
            string name,
            string description,
            NexExperienceMaturity maturity,
            string actor,
            Guid? experienceId = null)
        {
            if (ownerId == Guid.Empty)
                throw new ArgumentException("Experience owner is required.", nameof(ownerId));

            string normalizedName = NormalizeName(name);

            lock (m_Sync)
            {
                if (m_State.Experiences.Any(x =>
                    string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("An experience with that name already exists.");
                }

                Guid id = experienceId ?? Guid.NewGuid();
                if (id == Guid.Empty || m_State.Experiences.Any(x => x.ExperienceId == id))
                    throw new InvalidOperationException("Experience ID already exists.");

                NexExperience experience = new NexExperience
                {
                    ExperienceId = id,
                    OwnerId = ownerId,
                    GroupId = groupId,
                    Name = normalizedName,
                    Description = (description ?? string.Empty).Trim(),
                    Maturity = maturity,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                experience.Admins.Add(ownerId);

                m_State.Experiences.Add(experience);
                AddLog(experience.ExperienceId, actor, "experience.create", normalizedName);
                Save();
                return Clone(experience);
            }
        }

        public NexExperience Get(Guid experienceId)
        {
            lock (m_Sync)
                return Clone(Find(experienceId));
        }

        public IReadOnlyList<NexExperience> Search(string query, int offset, int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 500)
                throw new ArgumentOutOfRangeException(nameof(limit));

            string normalized = (query ?? string.Empty).Trim();

            lock (m_Sync)
            {
                IEnumerable<NexExperience> result =
                    m_State.Experiences.Where(x => x.Enabled);

                if (normalized.Length > 0)
                {
                    result = result.Where(x =>
                        x.Name.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        x.Description.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                return result
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public NexExperience UpdateProfile(
            Guid experienceId,
            Guid actorId,
            string name,
            string description,
            Guid groupId,
            NexExperienceMaturity maturity,
            bool enabled,
            string actor)
        {
            lock (m_Sync)
            {
                NexExperience experience = RequireManage(experienceId, actorId);
                string normalizedName = NormalizeName(name);

                if (m_State.Experiences.Any(x =>
                    x.ExperienceId != experienceId &&
                    string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("An experience with that name already exists.");
                }

                experience.Name = normalizedName;
                experience.Description = (description ?? string.Empty).Trim();
                experience.GroupId = groupId;
                experience.Maturity = maturity;
                experience.Enabled = enabled;
                experience.UpdatedAt = DateTimeOffset.UtcNow;
                AddLog(experienceId, actor, "experience.update", normalizedName);
                Save();
                return Clone(experience);
            }
        }

        public void Delete(Guid experienceId, Guid actorId, string actor)
        {
            lock (m_Sync)
            {
                NexExperience experience = Find(experienceId) ??
                    throw new KeyNotFoundException("Experience was not found.");

                if (experience.OwnerId != actorId)
                    throw new UnauthorizedAccessException("Only the experience owner can delete it.");

                m_State.Experiences.Remove(experience);

                foreach (Guid scriptId in m_State.ScriptBindings
                    .Where(x => x.Value == experienceId)
                    .Select(x => x.Key)
                    .ToArray())
                {
                    m_State.ScriptBindings.Remove(scriptId);
                }

                AddLog(experienceId, actor, "experience.delete", experience.Name);
                Save();
            }
        }

        public NexExperience SetRole(
            Guid experienceId,
            Guid actorId,
            Guid residentId,
            string role,
            bool enabled,
            string actor)
        {
            if (residentId == Guid.Empty)
                throw new ArgumentException("Resident ID is required.", nameof(residentId));

            lock (m_Sync)
            {
                NexExperience experience = RequireManage(experienceId, actorId);
                HashSet<Guid> target;

                if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
                    target = experience.Admins;
                else if (string.Equals(role, "contributor", StringComparison.OrdinalIgnoreCase))
                    target = experience.Contributors;
                else
                    throw new ArgumentException("Role must be admin or contributor.", nameof(role));

                if (enabled)
                    target.Add(residentId);
                else if (residentId != experience.OwnerId)
                    target.Remove(residentId);

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                AddLog(experienceId, actor, "experience.role", role + ":" + residentId + ":" + enabled);
                Save();
                return Clone(experience);
            }
        }

        public NexExperiencePermissionStatus GetResidentPermission(
            Guid experienceId,
            Guid residentId)
        {
            lock (m_Sync)
            {
                NexExperience experience = Find(experienceId);
                if (experience == null || !experience.Enabled)
                    return NexExperiencePermissionStatus.None;

                if (experience.BlockedResidents.Contains(residentId))
                    return NexExperiencePermissionStatus.Blocked;

                if (experience.OwnerId == residentId ||
                    experience.Admins.Contains(residentId) ||
                    experience.Contributors.Contains(residentId) ||
                    experience.AllowedResidents.Contains(residentId))
                {
                    return NexExperiencePermissionStatus.Allowed;
                }

                return NexExperiencePermissionStatus.None;
            }
        }

        public NexExperience SetResidentPermission(
            Guid experienceId,
            Guid actorId,
            Guid residentId,
            NexExperiencePermissionStatus status,
            string actor)
        {
            if (residentId == Guid.Empty)
                throw new ArgumentException("Resident ID is required.", nameof(residentId));

            lock (m_Sync)
            {
                NexExperience experience = RequireManage(experienceId, actorId);
                experience.AllowedResidents.Remove(residentId);
                experience.BlockedResidents.Remove(residentId);

                if (status == NexExperiencePermissionStatus.Allowed)
                    experience.AllowedResidents.Add(residentId);
                else if (status == NexExperiencePermissionStatus.Blocked)
                    experience.BlockedResidents.Add(residentId);

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                AddLog(experienceId, actor, "experience.permission",
                    residentId + ":" + status);
                Save();
                return Clone(experience);
            }
        }

        /// <summary>
        /// Resident-initiated consent/revocation via a trusted, separately
        /// scoped simulator endpoint. Unlike administrator moderation this
        /// never accepts an arbitrary actor or changes someone else's grant.
        /// Callers must verify the active viewer identity before forwarding.
        /// </summary>
        public NexExperience SetOwnResidentPermission(Guid experienceId,
            Guid residentId, NexExperiencePermissionStatus status, string actor)
        {
            if (residentId == Guid.Empty || experienceId == Guid.Empty)
                throw new ArgumentException("Experience and resident are required.");
            if (!Enum.IsDefined(typeof(NexExperiencePermissionStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));

            lock (m_Sync)
            {
                NexExperience experience = Find(experienceId);
                if (experience == null)
                    throw new KeyNotFoundException("Experience was not found.");
                if (!experience.Enabled)
                    throw new InvalidOperationException("Experience is disabled.");
                if (experience.OwnerId == residentId &&
                    status == NexExperiencePermissionStatus.Blocked)
                    throw new InvalidOperationException(
                        "Owners cannot block their own experience.");

                experience.AllowedResidents.Remove(residentId);
                experience.BlockedResidents.Remove(residentId);
                if (status == NexExperiencePermissionStatus.Allowed)
                    experience.AllowedResidents.Add(residentId);
                if (status == NexExperiencePermissionStatus.Blocked)
                    experience.BlockedResidents.Add(residentId);

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                AddLog(experienceId, actor, "experience.resident.consent",
                    residentId + ":" + status);
                Save();
                return Clone(experience);
            }
        }

        /// <summary>
        /// Return only this resident's own Experience lists. No owner lists,
        /// other users' permissions, script bindings or keys are exposed.
        /// </summary>
        public (Guid[] Allowed, Guid[] Blocked, Guid[] Owned, Guid[] Admin, Guid[] Contributor) GetResidentLists(Guid residentId)
        {
            if (residentId == Guid.Empty)
                throw new ArgumentException("Resident ID is required.", nameof(residentId));
            lock (m_Sync)
            {
                NexExperience[] active = m_State.Experiences
                    .Where(x => x.Enabled).ToArray();
                return (
                    active.Where(x => !x.BlockedResidents.Contains(residentId) &&
                        (x.AllowedResidents.Contains(residentId) ||
                         x.OwnerId == residentId || x.Admins.Contains(residentId) ||
                         x.Contributors.Contains(residentId)))
                        .Select(x => x.ExperienceId).ToArray(),
                    active.Where(x => x.BlockedResidents.Contains(residentId))
                        .Select(x => x.ExperienceId).ToArray(),
                    active.Where(x => x.OwnerId == residentId)
                        .Select(x => x.ExperienceId).ToArray(),
                    active.Where(x => x.Admins.Contains(residentId))
                        .Select(x => x.ExperienceId).ToArray(),
                    active.Where(x => x.Contributors.Contains(residentId))
                        .Select(x => x.ExperienceId).ToArray());
            }
        }

        /// <summary>Public enabled Experience IDs bound to a given group.</summary>
        public Guid[] GetGroupExperiences(Guid groupId)
        {
            if (groupId == Guid.Empty)
                throw new ArgumentException("Group UUID is required.", nameof(groupId));
            lock (m_Sync)
                return m_State.Experiences
                    .Where(x => x.Enabled && x.GroupId == groupId)
                    .Select(x => x.ExperienceId).ToArray();
        }

        public NexExperience SetLocationPolicy(
            Guid experienceId,
            Guid actorId,
            string kind,
            Guid locationId,
            bool allowed,
            bool enabled,
            string actor)
        {
            if (locationId == Guid.Empty)
                throw new ArgumentException("Location ID is required.", nameof(locationId));

            lock (m_Sync)
            {
                NexExperience experience = RequireManage(experienceId, actorId);
                HashSet<Guid> allow;
                HashSet<Guid> block;

                if (string.Equals(kind, "estate", StringComparison.OrdinalIgnoreCase))
                {
                    allow = experience.AllowedEstates;
                    block = experience.BlockedEstates;
                }
                else if (string.Equals(kind, "parcel", StringComparison.OrdinalIgnoreCase))
                {
                    allow = experience.AllowedParcels;
                    block = experience.BlockedParcels;
                }
                else
                {
                    throw new ArgumentException("Policy kind must be estate or parcel.", nameof(kind));
                }

                allow.Remove(locationId);
                block.Remove(locationId);

                if (enabled)
                {
                    if (allowed)
                        allow.Add(locationId);
                    else
                        block.Add(locationId);
                }

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                AddLog(experienceId, actor, "experience.location_policy",
                    kind + ":" + locationId + ":" + (allowed ? "allow" : "block") + ":" + enabled);
                Save();
                return Clone(experience);
            }
        }

        public bool IsLocationAllowed(
            Guid experienceId,
            Guid estateId,
            Guid parcelId)
        {
            lock (m_Sync)
            {
                NexExperience experience = Find(experienceId);
                if (experience == null || !experience.Enabled)
                    return false;

                if (estateId != Guid.Empty && experience.BlockedEstates.Contains(estateId))
                    return false;
                if (parcelId != Guid.Empty && experience.BlockedParcels.Contains(parcelId))
                    return false;

                if (experience.AllowedEstates.Count > 0 &&
                    (estateId == Guid.Empty || !experience.AllowedEstates.Contains(estateId)))
                {
                    return false;
                }

                if (experience.AllowedParcels.Count > 0 &&
                    (parcelId == Guid.Empty || !experience.AllowedParcels.Contains(parcelId)))
                {
                    return false;
                }

                return true;
            }
        }

        public void BindScript(
            Guid experienceId,
            Guid actorId,
            Guid scriptItemId,
            string actor)
        {
            if (scriptItemId == Guid.Empty)
                throw new ArgumentException("Script item ID is required.", nameof(scriptItemId));

            lock (m_Sync)
            {
                RequireManage(experienceId, actorId);
                m_State.ScriptBindings[scriptItemId] = experienceId;
                AddLog(experienceId, actor, "experience.script.bind", scriptItemId.ToString("D"));
                Save();
            }
        }

        public Guid ResolveScript(Guid scriptItemId)
        {
            lock (m_Sync)
                return m_State.ScriptBindings.TryGetValue(scriptItemId, out Guid id)
                    ? id
                    : Guid.Empty;
        }

        public bool CreateKeyValue(
            Guid experienceId,
            Guid scriptItemId,
            string key,
            string value)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                NexExperience experience = Find(experienceId);
                string normalized = ValidateKey(key);
                ValidateValue(value);

                if (experience.KeyValues.ContainsKey(normalized))
                    return false;
                if (experience.KeyValues.Count >= MaxKeysPerExperience)
                    throw new InvalidOperationException("Experience key/value quota exceeded.");

                experience.KeyValues.Add(normalized, value ?? string.Empty);

                if (GetStoreBytes(experience) > MaxStoreBytes)
                {
                    experience.KeyValues.Remove(normalized);
                    throw new InvalidOperationException("Experience key/value storage quota exceeded.");
                }

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                Save();
                return true;
            }
        }

        public bool TryReadKeyValue(
            Guid experienceId,
            Guid scriptItemId,
            string key,
            out string value)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                NexExperience experience = Find(experienceId);
                return experience.KeyValues.TryGetValue(ValidateKey(key), out value);
            }
        }

        public bool UpdateKeyValue(
            Guid experienceId,
            Guid scriptItemId,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            out bool retryMismatch)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                NexExperience experience = Find(experienceId);
                string normalized = ValidateKey(key);
                ValidateValue(value);
                retryMismatch = false;

                bool exists =
                    experience.KeyValues.TryGetValue(
                        normalized,
                        out string current);

                if (checkOriginal &&
                    exists &&
                    !string.Equals(
                        current,
                        originalValue ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    retryMismatch = true;
                    return false;
                }

                if (!exists &&
                    experience.KeyValues.Count >= MaxKeysPerExperience)
                {
                    throw new InvalidOperationException("Experience key/value quota exceeded.");
                }

                experience.KeyValues[normalized] = value ?? string.Empty;

                if (GetStoreBytes(experience) > MaxStoreBytes)
                {
                    if (exists)
                        experience.KeyValues[normalized] = current;
                    else
                        experience.KeyValues.Remove(normalized);

                    throw new InvalidOperationException("Experience key/value storage quota exceeded.");
                }

                experience.UpdatedAt = DateTimeOffset.UtcNow;
                Save();
                return true;
            }
        }

        public int GetKeyCount(
            Guid experienceId,
            Guid scriptItemId)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                return Find(experienceId).KeyValues.Count;
            }
        }

        public IReadOnlyList<string> ListKeys(
            Guid experienceId,
            Guid scriptItemId,
            int start,
            int count)
        {
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (count < 1 || count > 1024)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                return Find(experienceId).KeyValues.Keys
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Skip(start)
                    .Take(count)
                    .ToArray();
            }
        }

        public long GetDataSize(
            Guid experienceId,
            Guid scriptItemId)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                return GetStoreBytes(Find(experienceId));
            }
        }

        private static long GetStoreBytes(
            NexExperience experience)
        {
            long bytes = 0;

            foreach (KeyValuePair<string, string> entry in experience.KeyValues)
            {
                bytes += System.Text.Encoding.UTF8.GetByteCount(entry.Key);
                bytes += System.Text.Encoding.UTF8.GetByteCount(entry.Value ?? string.Empty);
            }

            return bytes;
        }

        public bool DeleteKeyValue(
            Guid experienceId,
            Guid scriptItemId,
            string key)
        {
            lock (m_Sync)
            {
                RequireBoundScript(experienceId, scriptItemId);
                NexExperience experience = Find(experienceId);
                bool removed = experience.KeyValues.Remove(ValidateKey(key));
                if (removed)
                {
                    experience.UpdatedAt = DateTimeOffset.UtcNow;
                    Save();
                }
                return removed;
            }
        }

        public IReadOnlyList<NexExperienceLogEntry> GetLogs(
            Guid experienceId,
            int offset,
            int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 500)
                throw new ArgumentOutOfRangeException(nameof(limit));

            lock (m_Sync)
            {
                return m_State.Logs
                    .Where(x => x.ExperienceId == experienceId)
                    .OrderByDescending(x => x.Timestamp)
                    .Skip(offset)
                    .Take(limit)
                    .Select(Clone)
                    .ToArray();
            }
        }

        private NexExperience RequireManage(Guid experienceId, Guid actorId)
        {
            NexExperience experience = Find(experienceId) ??
                throw new KeyNotFoundException("Experience was not found.");

            if (actorId == Guid.Empty ||
                (experience.OwnerId != actorId && !experience.Admins.Contains(actorId)))
            {
                throw new UnauthorizedAccessException("Experience management permission is required.");
            }

            return experience;
        }

        private void RequireBoundScript(Guid experienceId, Guid scriptItemId)
        {
            NexExperience experience = Find(experienceId);
            if (experience == null)
                throw new KeyNotFoundException("Experience was not found.");

            // Disabled Experiences must not keep executing K/V operations
            // through previously issued script bindings. Fail closed.
            if (!experience.Enabled)
                throw new InvalidOperationException("Experience is disabled.");

            if (!m_State.ScriptBindings.TryGetValue(scriptItemId, out Guid bound) ||
                bound != experienceId)
            {
                throw new UnauthorizedAccessException("Script is not bound to this experience.");
            }
        }

        private NexExperience Find(Guid id) =>
            m_State.Experiences.FirstOrDefault(x => x.ExperienceId == id);

        private static string NormalizeName(string name)
        {
            string normalized = (name ?? string.Empty).Trim();
            if (normalized.Length < 3 || normalized.Length > 64)
                throw new ArgumentOutOfRangeException(nameof(name), "Experience name must contain 3-64 characters.");
            return normalized;
        }

        private static string ValidateKey(string key)
        {
            string normalized = (key ?? string.Empty).Trim();

            if (normalized.Length == 0 ||
                System.Text.Encoding.UTF8.GetByteCount(normalized) > MaxKeyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(key));
            }

            return normalized;
        }

        private static void ValidateValue(string value)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(value ?? string.Empty) >
                MaxValueLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private void AddLog(Guid experienceId, string actor, string action, string detail)
        {
            m_State.Logs.Add(new NexExperienceLogEntry
            {
                ExperienceId = experienceId,
                Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
                Action = action ?? string.Empty,
                Detail = detail ?? string.Empty
            });

            if (m_State.Logs.Count > MaxLogs)
                m_State.Logs.RemoveRange(0, m_State.Logs.Count - MaxLogs);
        }

        private NexExperienceSnapshot Load()
        {
            if (!File.Exists(m_Path))
                return new NexExperienceSnapshot { SchemaVersion = CurrentSchemaVersion };

            NexExperienceSnapshot state =
                JsonSerializer.Deserialize<NexExperienceSnapshot>(
                    File.ReadAllText(m_Path),
                    m_Json) ??
                new NexExperienceSnapshot();

            if (state.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported experience store schema version.");

            state.Experiences ??= new List<NexExperience>();
            state.ScriptBindings ??= new Dictionary<Guid, Guid>();
            state.Logs ??= new List<NexExperienceLogEntry>();

            foreach (NexExperience experience in state.Experiences)
            {
                experience.Admins ??= new HashSet<Guid>();
                experience.Contributors ??= new HashSet<Guid>();
                experience.AllowedResidents ??= new HashSet<Guid>();
                experience.BlockedResidents ??= new HashSet<Guid>();
                experience.AllowedEstates ??= new HashSet<Guid>();
                experience.BlockedEstates ??= new HashSet<Guid>();
                experience.AllowedParcels ??= new HashSet<Guid>();
                experience.BlockedParcels ??= new HashSet<Guid>();
                experience.KeyValues ??= new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return state;
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(m_Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = m_Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(m_State, m_Json));
            File.Move(temp, m_Path, true);
        }

        private static NexExperience Clone(NexExperience source)
        {
            if (source == null)
                return null;

            return new NexExperience
            {
                ExperienceId = source.ExperienceId,
                Name = source.Name,
                Description = source.Description,
                OwnerId = source.OwnerId,
                GroupId = source.GroupId,
                Maturity = source.Maturity,
                Enabled = source.Enabled,
                Admins = new HashSet<Guid>(source.Admins),
                Contributors = new HashSet<Guid>(source.Contributors),
                AllowedResidents = new HashSet<Guid>(source.AllowedResidents),
                BlockedResidents = new HashSet<Guid>(source.BlockedResidents),
                AllowedEstates = new HashSet<Guid>(source.AllowedEstates),
                BlockedEstates = new HashSet<Guid>(source.BlockedEstates),
                AllowedParcels = new HashSet<Guid>(source.AllowedParcels),
                BlockedParcels = new HashSet<Guid>(source.BlockedParcels),
                KeyValues = new Dictionary<string, string>(source.KeyValues, StringComparer.Ordinal),
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt
            };
        }

        private static NexExperienceLogEntry Clone(NexExperienceLogEntry source) =>
            new NexExperienceLogEntry
            {
                LogId = source.LogId,
                ExperienceId = source.ExperienceId,
                Timestamp = source.Timestamp,
                Actor = source.Actor,
                Action = source.Action,
                Detail = source.Detail
            };
    }
}
