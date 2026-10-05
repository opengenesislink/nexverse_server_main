// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexVerse.Core.Jobs;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Performs a conservative structural inventory repair for one resident.
    /// The worker never deletes folders/items and never accesses the inventory
    /// database directly. Invalid folder parent relations are reattached to the
    /// authoritative inventory root. Dry-run is the default.
    /// </summary>
    internal sealed class OglInventoryRepairWorker : IOglJobWorker
    {
        private const int MaxRepairActions = 1000;
        private readonly IInventoryService m_Inventory;

        public OglInventoryRepairWorker(IInventoryService inventory)
        {
            m_Inventory = inventory;
        }

        public string JobType => "inventory.repair";

        public Task<IReadOnlyDictionary<string, string>> ExecuteAsync(
            OglJobContext context,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            if (m_Inventory == null)
                throw new InvalidOperationException(
                    "InventoryService ist fuer inventory.repair nicht verfuegbar.");

            string rawOwner = Required(parameters, "owner_id");
            if (!UUID.TryParse(rawOwner, out UUID owner) || owner == UUID.Zero)
                throw new ArgumentException(
                    "owner_id muss eine gueltige, von Null verschiedene UUID sein.");

            bool dryRun = OptionalBool(parameters, "dry_run", true);

            context.Progress(
                2,
                "inventory",
                "Inventarstruktur wird erfasst.");

            InventoryFolderBase root =
                m_Inventory.GetRootFolder(owner);

            if (!Owned(root, owner))
                throw new InvalidOperationException(
                    "Autoritativer Inventar-Root wurde nicht gefunden.");

            List<InventoryFolderBase> skeleton =
                m_Inventory.GetInventorySkeleton(owner)
                ?? new List<InventoryFolderBase>();

            Dictionary<UUID, InventoryFolderBase> folders =
                BuildFolderMap(owner, root, skeleton);

            context.Log(
                folders.Count +
                " Inventarordner fuer " +
                owner +
                " erfasst.");

            cancellationToken.ThrowIfCancellationRequested();

            List<RepairAction> actions =
                FindRepairActions(root, folders);

            if (actions.Count > MaxRepairActions)
            {
                throw new InvalidOperationException(
                    "Inventarreparatur abgebrochen: " +
                    actions.Count +
                    " strukturelle Fehler ueberschreiten das Sicherheitslimit von " +
                    MaxRepairActions +
                    " Aktionen.");
            }

            context.Progress(
                20,
                dryRun ? "dry_run" : "repairing",
                actions.Count +
                " strukturelle Reparaturaktion(en) geplant.");

            int repaired = 0;

            if (!dryRun)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    RepairAction action = actions[i];
                    InventoryFolderBase folder =
                        m_Inventory.GetFolder(owner, action.FolderId);

                    if (!Owned(folder, owner))
                    {
                        throw new InvalidOperationException(
                            "Ordner ist waehrend der Reparatur verschwunden: " +
                            action.FolderId);
                    }

                    if (folder.ID == root.ID)
                        throw new InvalidOperationException(
                            "Der Inventar-Root darf nicht verschoben werden.");

                    folder.ParentID = root.ID;

                    if (!m_Inventory.MoveFolder(folder))
                    {
                        throw new InvalidOperationException(
                            "Inventarordner konnte nicht zum Root verschoben werden: " +
                            folder.ID);
                    }

                    repaired++;

                    context.Log(
                        "Ordner " +
                        folder.ID +
                        " -> Root; Grund: " +
                        action.Reason +
                        ".");

                    context.Progress(
                        20 +
                        (int)(65L * repaired /
                              Math.Max(1, actions.Count)),
                        "repairing",
                        folder.ID.ToString());
                }
            }
            else
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.Log(
                        "Dry-Run: Ordner " +
                        actions[i].FolderId +
                        " wuerde zum Root verschoben; Grund: " +
                        actions[i].Reason +
                        ".");

                    context.Progress(
                        20 +
                        (int)(65L * (i + 1) /
                              Math.Max(1, actions.Count)),
                        "dry_run",
                        actions[i].FolderId.ToString());
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            context.Progress(
                90,
                "verifying",
                "Inventarstruktur wird erneut validiert.");

            int remainingIssues = actions.Count;

            if (!dryRun)
            {
                List<InventoryFolderBase> verifiedSkeleton =
                    m_Inventory.GetInventorySkeleton(owner)
                    ?? new List<InventoryFolderBase>();

                Dictionary<UUID, InventoryFolderBase> verifiedFolders =
                    BuildFolderMap(owner, root, verifiedSkeleton);

                remainingIssues =
                    FindRepairActions(root, verifiedFolders).Count;

                if (remainingIssues != 0)
                {
                    throw new InvalidOperationException(
                        "Inventarreparatur wurde nicht vollstaendig verifiziert; verbleibende strukturelle Fehler: " +
                        remainingIssues +
                        ".");
                }
            }

            context.Progress(
                97,
                dryRun ? "dry_run_completed" : "verified",
                dryRun
                    ? "Dry-Run abgeschlossen; Inventar wurde nicht veraendert."
                    : "Inventarstruktur ist nach der Reparatur konsistent.");

            IReadOnlyDictionary<string, string> result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["owner_id"] = owner.ToString(),
                    ["root_id"] = root.ID.ToString(),
                    ["folder_count"] = folders.Count.ToString(),
                    ["planned_repairs"] = actions.Count.ToString(),
                    ["repaired_folders"] = repaired.ToString(),
                    ["remaining_issues"] = remainingIssues.ToString(),
                    ["dry_run"] = dryRun.ToString(),
                    ["deletions"] = bool.FalseString,
                    ["direct_database_access"] = bool.FalseString
                };

            return Task.FromResult(result);
        }

        private static Dictionary<UUID, InventoryFolderBase> BuildFolderMap(
            UUID owner,
            InventoryFolderBase root,
            List<InventoryFolderBase> skeleton)
        {
            Dictionary<UUID, InventoryFolderBase> folders =
                new Dictionary<UUID, InventoryFolderBase>();

            if (Owned(root, owner))
                folders[root.ID] = root;

            foreach (InventoryFolderBase folder in skeleton)
            {
                if (!Owned(folder, owner) || folder.ID == UUID.Zero)
                    continue;

                folders[folder.ID] = folder;
            }

            return folders;
        }

        private static List<RepairAction> FindRepairActions(
            InventoryFolderBase root,
            Dictionary<UUID, InventoryFolderBase> folders)
        {
            List<RepairAction> actions =
                new List<RepairAction>();

            HashSet<UUID> planned =
                new HashSet<UUID>();

            List<InventoryFolderBase> ordered =
                new List<InventoryFolderBase>(
                    folders.Values);

            ordered.Sort(
                (left, right) =>
                    string.CompareOrdinal(
                        left.ID.ToString(),
                        right.ID.ToString()));

            foreach (InventoryFolderBase folder in ordered)
            {
                if (folder.ID == root.ID)
                    continue;

                if (folder.ParentID == folder.ID)
                {
                    Plan(
                        actions,
                        planned,
                        folder.ID,
                        "self_parent");
                    continue;
                }

                if (folder.ParentID == UUID.Zero ||
                    !folders.ContainsKey(folder.ParentID))
                {
                    Plan(
                        actions,
                        planned,
                        folder.ID,
                        "missing_parent");
                }
            }

            foreach (InventoryFolderBase start in ordered)
            {
                if (start.ID == root.ID ||
                    planned.Contains(start.ID))
                {
                    continue;
                }

                HashSet<UUID> path =
                    new HashSet<UUID>();

                UUID current = start.ID;

                while (current != root.ID &&
                       folders.TryGetValue(
                           current,
                           out InventoryFolderBase currentFolder))
                {
                    if (planned.Contains(current))
                        break;

                    if (!path.Add(current))
                    {
                        Plan(
                            actions,
                            planned,
                            current,
                            "parent_cycle");
                        break;
                    }

                    UUID parent = currentFolder.ParentID;

                    if (parent == root.ID)
                        break;

                    if (parent == UUID.Zero ||
                        !folders.ContainsKey(parent))
                    {
                        break;
                    }

                    current = parent;
                }
            }

            return actions;
        }

        private static void Plan(
            List<RepairAction> actions,
            HashSet<UUID> planned,
            UUID folderId,
            string reason)
        {
            if (planned.Add(folderId))
                actions.Add(
                    new RepairAction(
                        folderId,
                        reason));
        }

        private static bool Owned(
            InventoryFolderBase folder,
            UUID owner)
        {
            return
                folder != null &&
                folder.Owner == owner;
        }

        private static string Required(
            IReadOnlyDictionary<string, string> parameters,
            string key)
        {
            if (parameters != null &&
                parameters.TryGetValue(
                    key,
                    out string value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            throw new ArgumentException(
                "Job-Parameter fehlt: " +
                key);
        }

        private static bool OptionalBool(
            IReadOnlyDictionary<string, string> parameters,
            string key,
            bool fallback)
        {
            if (parameters != null &&
                parameters.TryGetValue(
                    key,
                    out string raw) &&
                bool.TryParse(
                    raw,
                    out bool value))
            {
                return value;
            }

            return fallback;
        }

        private sealed class RepairAction
        {
            public RepairAction(
                UUID folderId,
                string reason)
            {
                FolderId = folderId;
                Reason = reason ?? string.Empty;
            }

            public UUID FolderId { get; }
            public string Reason { get; }
        }
    }
}
