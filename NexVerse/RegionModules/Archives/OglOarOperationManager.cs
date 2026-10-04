// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Archives
{
    public enum OglOarOperationKind { Export, Import }
    public enum OglOarOperationState { Queued, Running, Completed, Failed }

    public sealed class OglOarOperation
    {
        public Guid RequestId { get; init; }
        public UUID RegionId { get; init; }
        public string RegionName { get; init; } = string.Empty;
        public OglOarOperationKind Kind { get; init; }
        public OglOarOperationState State { get; internal set; }
        public string ArchivePath { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; internal set; }
        public DateTimeOffset? FinishedAt { get; internal set; }
        public string Error { get; internal set; } = string.Empty;
        public OglOarInspection Inspection { get; internal set; }
    }

    public interface IOglOarOperations
    {
        OglOarOperation StartExport(string archiveFileName, Dictionary<string, object> options = null);
        OglOarOperation StartImport(string archiveFileName, bool dryRun, Dictionary<string, object> options = null);
        bool TryGet(Guid requestId, out OglOarOperation operation);
    }

    /// <summary>
    /// One authoritative orchestration path for OAR operations. The existing
    /// IRegionArchiverModule remains responsible for scene mutation and serialization.
    /// </summary>
    public sealed class OglOarOperationManager : IOglOarOperations, IDisposable
    {
        private readonly Scene m_Scene;
        private readonly IRegionArchiverModule m_Archiver;
        private readonly OglOarStoragePolicy m_Storage;
        private readonly ConcurrentDictionary<Guid, OglOarOperation> m_Operations = new();

        public OglOarOperationManager(Scene scene, OglOarStoragePolicy storage)
        {
            m_Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            m_Archiver = scene.RequestModuleInterface<IRegionArchiverModule>()
                ?? throw new InvalidOperationException("OAR-Archiver ist fuer die Region nicht verfuegbar.");

            m_Scene.EventManager.OnOarFileSaved += HandleSaved;
            m_Scene.EventManager.OnOarFileLoaded += HandleLoaded;
        }

        public OglOarOperation StartExport(string archiveFileName, Dictionary<string, object> options = null)
        {
            string path = m_Storage.ResolveArchivePath(archiveFileName);
            Directory.CreateDirectory(m_Storage.RootDirectory);
            Guid requestId = Guid.NewGuid();
            OglOarOperation operation = Create(requestId, OglOarOperationKind.Export, path);
            operation.State = OglOarOperationState.Running;
            operation.StartedAt = DateTimeOffset.UtcNow;

            try
            {
                m_Archiver.ArchiveRegion(path, requestId, options ?? new Dictionary<string, object>());
                return operation;
            }
            catch (Exception e)
            {
                Fail(operation, e.Message);
                throw;
            }
        }

        public OglOarOperation StartImport(string archiveFileName, bool dryRun, Dictionary<string, object> options = null)
        {
            string path = m_Storage.ResolveArchivePath(archiveFileName);
            if (!m_Storage.AcceptsExistingArchive(path, out string policyError))
                throw new InvalidOperationException(policyError);

            OglOarInspection inspection = OglOarInspector.Inspect(path);
            if (!inspection.Valid)
                throw new InvalidDataException(inspection.Error);

            Guid requestId = Guid.NewGuid();
            OglOarOperation operation = Create(requestId, OglOarOperationKind.Import, path);
            operation.Inspection = inspection;

            if (dryRun)
            {
                operation.State = OglOarOperationState.Completed;
                operation.StartedAt = operation.CreatedAt;
                operation.FinishedAt = DateTimeOffset.UtcNow;
                return operation;
            }

            operation.State = OglOarOperationState.Running;
            operation.StartedAt = DateTimeOffset.UtcNow;
            try
            {
                m_Archiver.DearchiveRegion(path, requestId, options ?? new Dictionary<string, object>());
                return operation;
            }
            catch (Exception e)
            {
                Fail(operation, e.Message);
                throw;
            }
        }

        public bool TryGet(Guid requestId, out OglOarOperation operation) =>
            m_Operations.TryGetValue(requestId, out operation);

        private OglOarOperation Create(Guid requestId, OglOarOperationKind kind, string path)
        {
            OglOarOperation operation = new()
            {
                RequestId = requestId,
                RegionId = m_Scene.RegionInfo.RegionID,
                RegionName = m_Scene.RegionInfo.RegionName,
                Kind = kind,
                State = OglOarOperationState.Queued,
                ArchivePath = path,
                CreatedAt = DateTimeOffset.UtcNow
            };
            if (!m_Operations.TryAdd(requestId, operation))
                throw new InvalidOperationException("OAR-Request-ID konnte nicht registriert werden.");
            return operation;
        }

        private void HandleSaved(Guid requestId, string message)
        {
            if (!m_Operations.TryGetValue(requestId, out OglOarOperation operation)
                || operation.Kind != OglOarOperationKind.Export)
                return;

            if (!string.IsNullOrWhiteSpace(message))
            {
                Fail(operation, message);
                return;
            }

            operation.Inspection = OglOarInspector.Inspect(operation.ArchivePath);
            if (!operation.Inspection.Valid)
            {
                Fail(operation, operation.Inspection.Error);
                return;
            }

            operation.State = OglOarOperationState.Completed;
            operation.FinishedAt = DateTimeOffset.UtcNow;
        }

        private void HandleLoaded(Guid requestId, List<UUID> loadedScenes, string message)
        {
            if (!m_Operations.TryGetValue(requestId, out OglOarOperation operation)
                || operation.Kind != OglOarOperationKind.Import)
                return;

            if (!string.IsNullOrWhiteSpace(message))
            {
                Fail(operation, message);
                return;
            }

            operation.State = OglOarOperationState.Completed;
            operation.FinishedAt = DateTimeOffset.UtcNow;
        }

        private static void Fail(OglOarOperation operation, string error)
        {
            operation.State = OglOarOperationState.Failed;
            operation.Error = string.IsNullOrWhiteSpace(error) ? "Unbekannter OAR-Fehler." : error;
            operation.FinishedAt = DateTimeOffset.UtcNow;
        }

        public void Dispose()
        {
            m_Scene.EventManager.OnOarFileSaved -= HandleSaved;
            m_Scene.EventManager.OnOarFileLoaded -= HandleLoaded;
        }
    }
}
