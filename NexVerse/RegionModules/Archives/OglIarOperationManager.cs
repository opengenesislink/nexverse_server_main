// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace NexVerse.RegionModules.Archives
{
    public enum OglIarOperationKind { Export, Import }
    public enum OglIarOperationState { Queued, Running, Completed, Failed }

    public sealed class OglIarOperation
    {
        public UUID RequestId { get; init; }
        public OglIarOperationKind Kind { get; init; }
        public OglIarOperationState State { get; internal set; }
        public string UserName { get; init; } = string.Empty;
        public string InventoryPath { get; init; } = string.Empty;
        public string ArchivePath { get; init; } = string.Empty;
        public bool Merge { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; internal set; }
        public int ItemCount { get; internal set; }
        public int FilteredCount { get; internal set; }
        public string Error { get; internal set; } = string.Empty;
        public OglIarInspection Inspection { get; internal set; }
    }

    public interface IOglIarOperations
    {
        OglIarOperation StartExport(UUID requestId, string firstName, string lastName, string inventoryPath, string password, string archiveFileName, Dictionary<string, object> options = null);
        OglIarOperation StartImport(UUID requestId, string firstName, string lastName, string inventoryPath, string password, string archiveFileName, bool merge, bool dryRun);
        bool TryGet(UUID requestId, out OglIarOperation operation);
        event Action<OglIarOperation> OperationChanged;
    }

    public sealed class OglIarOperationManager : IOglIarOperations, IDisposable
    {
        private readonly IInventoryArchiverModule m_Archiver;
        private readonly OglIarStoragePolicy m_Storage;
        private readonly ConcurrentDictionary<UUID, OglIarOperation> m_Operations = new();

        public OglIarOperationManager(Scene scene, OglIarStoragePolicy storage)
        {
            m_Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            m_Archiver = scene?.RequestModuleInterface<IInventoryArchiverModule>()
                ?? throw new InvalidOperationException("IAR-Archiver ist nicht verfuegbar.");
            m_Archiver.OnInventoryArchiveSaved += HandleSaved;
            m_Archiver.OnInventoryArchiveLoaded += HandleLoaded;
        }

        public event Action<OglIarOperation> OperationChanged;

        public OglIarOperation StartExport(UUID requestId, string firstName, string lastName, string inventoryPath, string password, string archiveFileName, Dictionary<string, object> options = null)
        {
            string path = m_Storage.ResolveArchivePath(archiveFileName);
            Directory.CreateDirectory(m_Storage.RootDirectory);
            OglIarOperation operation = Create(requestId, OglIarOperationKind.Export, firstName, lastName, inventoryPath, path, false);
            operation.State = OglIarOperationState.Running;
            FileStream output = File.Create(path);
            try
            {
                if (!m_Archiver.ArchiveInventory(requestId, firstName, lastName, inventoryPath, password, output, options ?? new Dictionary<string, object>()))
                {
                    output.Dispose();
                    Fail(operation, "IAR-Export wurde vom Inventory-Archiver abgelehnt.");
                }
                return operation;
            }
            catch (Exception e)
            {
                output.Dispose();
                Fail(operation, e.Message);
                throw;
            }
        }

        public OglIarOperation StartImport(UUID requestId, string firstName, string lastName, string inventoryPath, string password, string archiveFileName, bool merge, bool dryRun)
        {
            string path = m_Storage.ResolveArchivePath(archiveFileName);
            if (!m_Storage.AcceptsExistingArchive(path, out string reason))
                throw new InvalidOperationException(reason);
            OglIarInspection inspection = OglIarInspector.Inspect(path);
            if (!inspection.Valid) throw new InvalidDataException(inspection.Error);

            OglIarOperation operation = Create(requestId, OglIarOperationKind.Import, firstName, lastName, inventoryPath, path, merge);
            operation.Inspection = inspection;
            if (dryRun)
            {
                operation.State = OglIarOperationState.Completed;
                operation.FinishedAt = DateTimeOffset.UtcNow;
                OperationChanged?.Invoke(operation);
                return operation;
            }

            operation.State = OglIarOperationState.Running;
            FileStream input = File.OpenRead(path);
            try
            {
                Dictionary<string, object> options = new() { ["merge"] = merge };
                if (!m_Archiver.DearchiveInventory(requestId, firstName, lastName, inventoryPath, password, input, options))
                {
                    input.Dispose();
                    Fail(operation, "IAR-Import wurde vom Inventory-Archiver abgelehnt.");
                }
                return operation;
            }
            catch (Exception e)
            {
                input.Dispose();
                Fail(operation, e.Message);
                throw;
            }
        }

        public bool TryGet(UUID requestId, out OglIarOperation operation) => m_Operations.TryGetValue(requestId, out operation);

        private OglIarOperation Create(UUID id, OglIarOperationKind kind, string first, string last, string invPath, string archivePath, bool merge)
        {
            if (id == UUID.Zero) throw new ArgumentException("IAR-Request-ID darf nicht leer sein.", nameof(id));
            OglIarOperation operation = new()
            {
                RequestId = id, Kind = kind, State = OglIarOperationState.Queued,
                UserName = (first + " " + last).Trim(), InventoryPath = invPath ?? string.Empty,
                ArchivePath = archivePath, Merge = merge, CreatedAt = DateTimeOffset.UtcNow
            };
            if (!m_Operations.TryAdd(id, operation)) throw new InvalidOperationException("IAR-Request-ID ist bereits registriert.");
            return operation;
        }

        private void HandleSaved(UUID id, bool succeeded, UserAccount user, string invPath, Stream stream, Exception error, int saveCount, int filterCount)
        {
            stream?.Dispose();
            if (!m_Operations.TryGetValue(id, out OglIarOperation operation) || operation.Kind != OglIarOperationKind.Export) return;
            operation.ItemCount = saveCount; operation.FilteredCount = filterCount;
            if (!succeeded || error != null) { Fail(operation, error?.Message ?? "IAR-Export fehlgeschlagen."); return; }
            operation.Inspection = OglIarInspector.Inspect(operation.ArchivePath);
            if (!operation.Inspection.Valid) { Fail(operation, operation.Inspection.Error); return; }
            Complete(operation);
        }

        private void HandleLoaded(UUID id, bool succeeded, UserAccount user, string invPath, Stream stream, Exception error, int loadCount)
        {
            stream?.Dispose();
            if (!m_Operations.TryGetValue(id, out OglIarOperation operation) || operation.Kind != OglIarOperationKind.Import) return;
            operation.ItemCount = loadCount;
            if (!succeeded || error != null) { Fail(operation, error?.Message ?? "IAR-Import fehlgeschlagen."); return; }
            Complete(operation);
        }

        private void Complete(OglIarOperation operation)
        {
            operation.State = OglIarOperationState.Completed; operation.FinishedAt = DateTimeOffset.UtcNow; OperationChanged?.Invoke(operation);
        }

        private void Fail(OglIarOperation operation, string error)
        {
            operation.State = OglIarOperationState.Failed; operation.Error = error ?? "Unbekannter IAR-Fehler."; operation.FinishedAt = DateTimeOffset.UtcNow; OperationChanged?.Invoke(operation);
        }

        public void Dispose()
        {
            m_Archiver.OnInventoryArchiveSaved -= HandleSaved;
            m_Archiver.OnInventoryArchiveLoaded -= HandleLoaded;
        }
    }
}
