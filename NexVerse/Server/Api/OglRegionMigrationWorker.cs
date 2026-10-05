// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Jobs;
using NexVerse.Core.Messaging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Moves one managed region between two NodeAgent hosts by using the
    /// authoritative OAR and managed-region control paths. Migration requires
    /// both nodes to advertise the same non-empty MigrationStorageId, which
    /// represents a shared OAR storage backend mounted on both simulators.
    ///
    /// The worker is intentionally non-cancellable once queued. Cancelling
    /// during the cutover could otherwise leave the same region split across
    /// two nodes. Failures after source shutdown execute a best-effort rollback.
    /// </summary>
    internal sealed class OglRegionMigrationWorker :
        IOglJobWorker,
        IOglNonCancellableJobWorker
    {
        private readonly NexNodeRegistry m_Nodes;
        private readonly INexEventBus m_Bus;
        private readonly IGridService m_Grid;
        private readonly IEstateDataService m_Estates;
        private readonly TimeSpan m_OperationTimeout;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> m_RegionLocks =
            new(StringComparer.OrdinalIgnoreCase);

        public OglRegionMigrationWorker(
            NexNodeRegistry nodes,
            INexEventBus bus,
            IGridService grid,
            IEstateDataService estates,
            int operationTimeoutSeconds)
        {
            m_Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            m_Bus = bus ?? throw new ArgumentNullException(nameof(bus));
            m_Grid = grid;
            m_Estates = estates;
            m_OperationTimeout =
                TimeSpan.FromSeconds(Math.Clamp(operationTimeoutSeconds, 30, 7200));
        }

        public string JobType => "regions.migrate";

        public async Task<IReadOnlyDictionary<string, string>> ExecuteAsync(
            OglJobContext context,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            string regionIdRaw = Required(parameters, "region_id");
            string targetNodeId = Required(parameters, "target_node_id");
            bool dryRun = OptionalBool(parameters, "dry_run");

            if (!UUID.TryParse(regionIdRaw, out UUID regionId) || regionId.IsZero())
                throw new ArgumentException("region_id muss eine gueltige, nicht leere UUID sein.");

            SemaphoreSlim regionLock =
                m_RegionLocks.GetOrAdd(regionId.ToString(), _ => new SemaphoreSlim(1, 1));

            await regionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteLockedAsync(
                        context,
                        regionId,
                        targetNodeId,
                        dryRun,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                regionLock.Release();
            }
        }

        private async Task<IReadOnlyDictionary<string, string>> ExecuteLockedAsync(
            OglJobContext context,
            UUID regionId,
            string targetNodeId,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            context.Progress(2, "preflight", "Region-Migration wird validiert.");

            if (m_Grid == null)
                throw new InvalidOperationException("GridService ist fuer Region-Migration nicht verfuegbar.");
            if (m_Estates == null)
                throw new InvalidOperationException("EstateDataStore ist fuer Region-Migration nicht verfuegbar.");

            NexNodeSnapshot sourceNode =
                m_Nodes.FindNodeForRegion(regionId.ToString());
            NexNodeSnapshot targetNode =
                m_Nodes.Get(targetNodeId);

            ValidateSource(sourceNode, regionId);
            ValidateTarget(targetNode, targetNodeId, regionId);

            if (string.Equals(
                    sourceNode.NodeId,
                    targetNode.NodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Quell- und Ziel-Node duerfen bei einer Region-Migration nicht identisch sein.");
            }

            if (string.IsNullOrWhiteSpace(sourceNode.MigrationStorageId) ||
                string.IsNullOrWhiteSpace(targetNode.MigrationStorageId) ||
                !string.Equals(
                    sourceNode.MigrationStorageId,
                    targetNode.MigrationStorageId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Quelle und Ziel muessen dieselbe nicht leere OpenGenesisLINK MigrationStorageId melden.");
            }

            OpenSim.Services.Interfaces.GridRegion gridRegion =
                m_Grid.GetRegionByUUID(UUID.Zero, regionId);

            if (gridRegion == null)
                throw new InvalidOperationException("Region wurde im GridService nicht gefunden.");

            EstateSettings estate =
                m_Estates.LoadEstateSettings(regionId, false);

            if (estate == null ||
                estate.EstateID == 0 ||
                estate.EstateID > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Estate-Zuordnung der Region konnte nicht bestimmt oder nicht sicher verarbeitet werden.");
            }

            int estateId = checked((int)estate.EstateID);

            NexNodeRegionSnapshot sourceRegion =
                sourceNode.Regions.FirstOrDefault(x =>
                    string.Equals(
                        x.RegionId,
                        regionId.ToString(),
                        StringComparison.OrdinalIgnoreCase));

            if (sourceRegion == null)
                throw new InvalidOperationException("Region ist auf dem Quell-Node nicht mehr aktiv.");

            if (sourceRegion.AgentCount > 0)
                throw new InvalidOperationException("Region enthaelt Root-Agents und kann nicht migriert werden.");

            string regionName =
                string.IsNullOrWhiteSpace(gridRegion.RegionName)
                    ? sourceRegion.Name
                    : gridRegion.RegionName;

            if (string.IsNullOrWhiteSpace(regionName))
                throw new InvalidOperationException("Regionsname konnte nicht bestimmt werden.");

            int gridX = FloorDiv(gridRegion.RegionLocX, (int)Constants.RegionSize);
            int gridY = FloorDiv(gridRegion.RegionLocY, (int)Constants.RegionSize);
            int sizeX = NormalizeSize(gridRegion.RegionSizeX);
            int sizeY = NormalizeSize(gridRegion.RegionSizeY);

            context.Log(
                $"Preflight OK: {regionName} ({regionId}) {sourceNode.NodeId} -> {targetNode.NodeId}, " +
                $"Grid {gridX},{gridY}, {sizeX}x{sizeY}, Estate {estateId}, " +
                $"Storage {sourceNode.MigrationStorageId}.");

            if (dryRun)
            {
                context.Progress(95, "preflight_completed", "Dry-Run erfolgreich; keine Region wurde veraendert.");
                return Result(
                    regionId,
                    regionName,
                    sourceNode.NodeId,
                    targetNode.NodeId,
                    sourceNode.MigrationStorageId,
                    estateId,
                    gridX,
                    gridY,
                    sizeX,
                    sizeY,
                    string.Empty,
                    string.Empty,
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string archiveName =
                "migration-" +
                regionId +
                "-" +
                context.JobId +
                ".oar";

            bool sourceStopped = false;
            bool targetCreated = false;

            try
            {
                context.Progress(8, "exporting", "Quellregion wird als OAR gesichert.");
                NexEvent exportCompleted = await OarAsync(
                        sourceNode.NodeId,
                        regionId,
                        "export",
                        archiveName,
                        context,
                        8,
                        30,
                        cancellationToken)
                    .ConfigureAwait(false);

                string exportHash = Data(exportCompleted, "archive_sha256");
                if (!IsSha256(exportHash) ||
                    !string.Equals(Data(exportCompleted, "archive_valid"), "True", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Quell-OAR wurde nicht mit gueltiger SHA-256-Integritaetsinformation bestaetigt.");
                }

                context.Log("Quell-OAR SHA-256: " + exportHash);
                cancellationToken.ThrowIfCancellationRequested();

                context.Progress(34, "stopping_source", "Quellregion wird kontrolliert gestoppt.");
                await LifecycleAsync(
                        sourceNode.NodeId,
                        regionId,
                        "stop",
                        "stopping_source",
                        cancellationToken)
                    .ConfigureAwait(false);
                sourceStopped = true;

                context.Progress(46, "starting_target", "Zielregion wird mit identischer UUID angelegt.");
                await CreateTargetAsync(
                        targetNode.NodeId,
                        regionId,
                        regionName,
                        gridX,
                        gridY,
                        sizeX,
                        sizeY,
                        estateId,
                        cancellationToken)
                    .ConfigureAwait(false);
                targetCreated = true;

                context.Progress(62, "importing", "OAR wird auf dem Ziel-Node importiert.");
                NexEvent importCompleted = await OarAsync(
                        targetNode.NodeId,
                        regionId,
                        "import",
                        archiveName,
                        context,
                        62,
                        86,
                        cancellationToken)
                    .ConfigureAwait(false);

                string importHash = Data(importCompleted, "archive_sha256");
                if (!IsSha256(importHash) ||
                    !string.Equals(exportHash, importHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "SHA-256-Pruefung zwischen exportiertem und importiertem Migrations-OAR ist fehlgeschlagen.");
                }

                context.Progress(90, "verifying_target", "Zielzuordnung wird vor dem finalen Cutover geprueft.");
                NexNodeSnapshot observed =
                    await WaitForRegionOnNodeAsync(
                            regionId,
                            targetNode.NodeId,
                            cancellationToken)
                        .ConfigureAwait(false);

                context.Progress(96, "retiring_source", "Alte Quell-Konfiguration wird nach erfolgreicher Zielpruefung stillgelegt.");
                await LifecycleAsync(
                        sourceNode.NodeId,
                        regionId,
                        "retire",
                        "retiring_source",
                        cancellationToken)
                    .ConfigureAwait(false);
                sourceStopped = false;

                context.Log(
                    $"Migration abgeschlossen: {regionName} laeuft jetzt auf {observed.NodeId}; " +
                    $"OAR SHA-256 {importHash}.");

                return Result(
                    regionId,
                    regionName,
                    sourceNode.NodeId,
                    targetNode.NodeId,
                    sourceNode.MigrationStorageId,
                    estateId,
                    gridX,
                    gridY,
                    sizeX,
                    sizeY,
                    archiveName,
                    importHash,
                    false);
            }
            catch (Exception e)
            {
                if (sourceStopped)
                {
                    string rollback = await RollbackAsync(
                            context,
                            regionId,
                            sourceNode.NodeId,
                            targetNode.NodeId,
                            targetCreated)
                        .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        e.Message + " Rollback: " + rollback,
                        e);
                }

                throw;
            }
        }

        private static void ValidateSource(
            NexNodeSnapshot node,
            UUID regionId)
        {
            if (node == null)
                throw new InvalidOperationException("Quell-Node der Region wurde nicht gefunden.");
            if (!string.Equals(node.State, "online", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Quell-Node ist nicht online.");
            if (!node.ManagedRegionCommands)
                throw new InvalidOperationException("Quell-Node erlaubt keine verwalteten Regionsbefehle.");
            if (!node.Regions.Any(x =>
                    string.Equals(
                        x.RegionId,
                        regionId.ToString(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Quell-Node meldet die Region nicht mehr.");
            }
        }

        private static void ValidateTarget(
            NexNodeSnapshot node,
            string targetNodeId,
            UUID regionId)
        {
            if (node == null)
                throw new InvalidOperationException("Ziel-Node wurde nicht gefunden: " + targetNodeId);
            if (!string.Equals(node.State, "online", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ziel-Node ist nicht online.");
            if (!node.ManagedRegionCommands)
                throw new InvalidOperationException("Ziel-Node erlaubt keine verwalteten Regionsbefehle.");
            if (node.Draining || node.MaintenanceMode)
                throw new InvalidOperationException("Ziel-Node befindet sich in Drain/Maintenance und nimmt keine Migration an.");
            if (node.Regions.Any(x =>
                    string.Equals(
                        x.RegionId,
                        regionId.ToString(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Ziel-Node meldet die Region bereits.");
            }
        }

        private async Task<NexEvent> OarAsync(
            string nodeId,
            UUID regionId,
            string action,
            string archiveName,
            OglJobContext context,
            int progressStart,
            int progressEnd,
            CancellationToken cancellationToken)
        {
            string operationId = Guid.NewGuid().ToString();
            Dictionary<string, string> data =
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["operation_id"] = operationId,
                    ["target_node_id"] = nodeId,
                    ["region_id"] = regionId.ToString(),
                    ["action"] = action,
                    ["file_name"] = archiveName,
                    ["dry_run"] = bool.FalseString
                };

            return await PublishAndWaitAsync(
                    new NexEvent(
                        "archive.oar.requested",
                        "opengenesislink.region-migration",
                        data,
                        operationId),
                    nodeId,
                    operationId,
                    "archive.oar.operation.",
                    e =>
                    {
                        if (int.TryParse(Data(e, "progress"), out int remoteProgress))
                        {
                            int projected =
                                progressStart +
                                (int)((progressEnd - progressStart) *
                                      Math.Clamp(remoteProgress, 0, 100) /
                                      100.0);
                            context.Progress(
                                projected,
                                action == "export" ? "exporting" : "importing",
                                Data(e, "phase"));
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task LifecycleAsync(
            string nodeId,
            UUID regionId,
            string action,
            string phase,
            CancellationToken cancellationToken)
        {
            string operationId = Guid.NewGuid().ToString("N");
            await PublishAndWaitAsync(
                    new NexEvent(
                        "region.control.lifecycle.requested",
                        "opengenesislink.region-migration",
                        new Dictionary<string, string>
                        {
                            ["operation_id"] = operationId,
                            ["target_node_id"] = nodeId,
                            ["region_id"] = regionId.ToString(),
                            ["action"] = action
                        },
                        operationId),
                    nodeId,
                    operationId,
                    "region.control.operation.",
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task CreateTargetAsync(
            string nodeId,
            UUID regionId,
            string regionName,
            int gridX,
            int gridY,
            int sizeX,
            int sizeY,
            int estateId,
            CancellationToken cancellationToken)
        {
            string operationId = Guid.NewGuid().ToString("N");
            await PublishAndWaitAsync(
                    new NexEvent(
                        "region.control.create.requested",
                        "opengenesislink.region-migration",
                        new Dictionary<string, string>
                        {
                            ["operation_id"] = operationId,
                            ["target_node_id"] = nodeId,
                            ["region_id"] = regionId.ToString(),
                            ["region_name"] = regionName,
                            ["grid_x"] = gridX.ToString(),
                            ["grid_y"] = gridY.ToString(),
                            ["size_x"] = sizeX.ToString(),
                            ["size_y"] = sizeY.ToString(),
                            ["estate_id"] = estateId.ToString()
                        },
                        operationId),
                    nodeId,
                    operationId,
                    "region.control.operation.",
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<NexEvent> PublishAndWaitAsync(
            NexEvent command,
            string expectedNodeId,
            string operationId,
            string eventPrefix,
            Action<NexEvent> progress,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<NexEvent> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            IDisposable subscription = null;
            using CancellationTokenSource timeout = new(m_OperationTimeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);

            void Handler(NexEvent e)
            {
                if (e?.Data == null ||
                    !e.Data.TryGetValue("operation_id", out string eventOperationId) ||
                    !string.Equals(eventOperationId, operationId, StringComparison.OrdinalIgnoreCase) ||
                    !(e.Name ?? string.Empty).StartsWith(eventPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (e.Data.TryGetValue("node_id", out string eventNodeId) &&
                    !string.Equals(eventNodeId, expectedNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                progress?.Invoke(e);

                string eventName = e.Name ?? string.Empty;

                if (eventName.EndsWith(".completed", StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult(e);
                }
                else if (eventName.EndsWith(".failed", StringComparison.OrdinalIgnoreCase))
                {
                    string message = Data(e, "message");
                    completion.TrySetException(
                        new InvalidOperationException(
                            string.IsNullOrWhiteSpace(message)
                                ? "Remote-Operation ist fehlgeschlagen."
                                : message));
                }
                else if (eventName.EndsWith(".cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetException(
                        new InvalidOperationException("Remote-Operation wurde abgebrochen."));
                }
            }

            subscription = m_Bus.Subscribe("*", Handler);
            using CancellationTokenRegistration registration =
                linked.Token.Register(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        completion.TrySetCanceled(cancellationToken);
                    else
                        completion.TrySetException(
                            new TimeoutException(
                                "Remote-Operation hat das Zeitlimit ueberschritten: " +
                                operationId));
                });

            try
            {
                m_Bus.Publish(command);
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                subscription?.Dispose();
            }
        }

        private async Task<NexNodeSnapshot> WaitForRegionOnNodeAsync(
            UUID regionId,
            string nodeId,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline =
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                NexNodeSnapshot node = m_Nodes.Get(nodeId);
                if (node != null &&
                    string.Equals(node.State, "online", StringComparison.OrdinalIgnoreCase) &&
                    node.Regions.Any(x =>
                        string.Equals(
                            x.RegionId,
                            regionId.ToString(),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return node;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                "Ziel-Node hat die migrierte Region nicht innerhalb des Verifikationsfensters gemeldet.");
        }

        private async Task<string> RollbackAsync(
            OglJobContext context,
            UUID regionId,
            string sourceNodeId,
            string targetNodeId,
            bool targetCreated)
        {
            List<string> notes = new();
            context.Log("Migration fehlgeschlagen; Rollback wird ausgefuehrt.");

            if (targetCreated)
            {
                try
                {
                    await LifecycleAsync(
                            targetNodeId,
                            regionId,
                            "stop",
                            "rollback_target_stop",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    notes.Add("Ziel gestoppt");
                }
                catch (Exception e)
                {
                    notes.Add("Ziel-Stopp fehlgeschlagen: " + e.Message);
                }

                try
                {
                    await LifecycleAsync(
                            targetNodeId,
                            regionId,
                            "retire",
                            "rollback_target_retire",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    notes.Add("Ziel-Konfiguration entfernt");
                }
                catch (Exception e)
                {
                    notes.Add("Ziel-Retire fehlgeschlagen: " + e.Message);
                }
            }

            try
            {
                await LifecycleAsync(
                        sourceNodeId,
                        regionId,
                        "start",
                        "rollback_source_start",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                notes.Add("Quelle wieder gestartet");
            }
            catch (Exception e)
            {
                notes.Add("QUELL-NEUSTART FEHLGESCHLAGEN: " + e.Message);
            }

            string result = string.Join("; ", notes);
            context.Log("Rollback-Ergebnis: " + result);
            return result;
        }

        private static Dictionary<string, string> Result(
            UUID regionId,
            string regionName,
            string sourceNodeId,
            string targetNodeId,
            string storageId,
            int estateId,
            int gridX,
            int gridY,
            int sizeX,
            int sizeY,
            string archiveName,
            string archiveSha256,
            bool dryRun)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["region_id"] = regionId.ToString(),
                ["region_name"] = regionName ?? string.Empty,
                ["source_node_id"] = sourceNodeId ?? string.Empty,
                ["target_node_id"] = targetNodeId ?? string.Empty,
                ["migration_storage_id"] = storageId ?? string.Empty,
                ["estate_id"] = estateId.ToString(),
                ["grid_x"] = gridX.ToString(),
                ["grid_y"] = gridY.ToString(),
                ["size_x"] = sizeX.ToString(),
                ["size_y"] = sizeY.ToString(),
                ["archive_name"] = archiveName ?? string.Empty,
                ["archive_sha256"] = archiveSha256 ?? string.Empty,
                ["dry_run"] = dryRun.ToString()
            };
        }

        private static int NormalizeSize(int size)
        {
            int cell = (int)Constants.RegionSize;
            return size > 0 ? size : cell;
        }

        private static int FloorDiv(long value, int divisor)
        {
            long quotient = value / divisor;
            long remainder = value % divisor;
            if (remainder != 0 && value < 0)
                quotient--;
            return checked((int)quotient);
        }

        private static string Required(
            IReadOnlyDictionary<string, string> parameters,
            string key)
        {
            if (parameters != null &&
                parameters.TryGetValue(key, out string value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            throw new ArgumentException("Job-Parameter fehlt: " + key);
        }

        private static bool OptionalBool(
            IReadOnlyDictionary<string, string> parameters,
            string key)
        {
            return parameters != null &&
                   parameters.TryGetValue(key, out string raw) &&
                   bool.TryParse(raw, out bool value) &&
                   value;
        }

        private static string Data(NexEvent e, string key)
        {
            return e?.Data != null &&
                   e.Data.TryGetValue(key, out string value)
                ? value ?? string.Empty
                : string.Empty;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                return false;

            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
