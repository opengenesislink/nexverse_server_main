// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexVerse.Core.Messaging;

namespace NexVerse.Core.ControlPlane
{
    public sealed class NexRegionOperationSnapshot
    {
        public NexRegionOperationSnapshot(
            string operationId,
            string operation,
            string nodeId,
            string regionId,
            string state,
            string message,
            string correlationId,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            IReadOnlyDictionary<string, string> details)
        {
            OperationId = operationId ?? string.Empty;
            Operation = operation ?? string.Empty;
            NodeId = nodeId ?? string.Empty;
            RegionId = regionId ?? string.Empty;
            State = state ?? "unknown";
            Message = message ?? string.Empty;
            CorrelationId = correlationId ?? string.Empty;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            Details = details ?? new Dictionary<string, string>();
        }

        public string OperationId { get; }
        public string Operation { get; }
        public string NodeId { get; }
        public string RegionId { get; }
        public string State { get; }
        public string Message { get; }
        public string CorrelationId { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; }
        public IReadOnlyDictionary<string, string> Details { get; }
    }

    /// <summary>
    /// Robust-side transient projection for asynchronous managed-region commands.
    /// It intentionally remains in-memory until the later Job Engine owns
    /// persistent long-running operation state.
    /// </summary>
    public sealed class NexRegionOperationRegistry : IDisposable
    {
        private const int MaximumEntries = 2048;

        private readonly ConcurrentDictionary<string, OperationRecord> m_Operations =
            new ConcurrentDictionary<string, OperationRecord>(
                StringComparer.OrdinalIgnoreCase);

        private readonly IDisposable m_Subscription;
        private int m_Disposed;

        public NexRegionOperationRegistry(INexEventBus eventBus)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));

            m_Subscription =
                eventBus.Subscribe(
                    "*",
                    HandleEvent);
        }

        public NexRegionOperationSnapshot Register(
            string operationId,
            string operation,
            string nodeId,
            string regionId,
            string correlationId,
            IReadOnlyDictionary<string, string> details = null)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation ID is required.", nameof(operationId));

            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            OperationRecord record =
                new OperationRecord(
                    operationId.Trim(),
                    operation,
                    nodeId,
                    regionId,
                    correlationId,
                    now,
                    details);

            if (!m_Operations.TryAdd(
                    record.OperationId,
                    record))
            {
                throw new InvalidOperationException(
                    "Region operation already exists: " +
                    record.OperationId);
            }

            Trim();
            return Snapshot(record);
        }

        public NexRegionOperationSnapshot Get(
            string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId) ||
                !m_Operations.TryGetValue(
                    operationId.Trim(),
                    out OperationRecord record))
            {
                return null;
            }

            return Snapshot(record);
        }

        private void HandleEvent(
            NexEvent nexEvent)
        {
            if (nexEvent == null ||
                nexEvent.Data == null ||
                !nexEvent.Data.TryGetValue(
                    "operation_id",
                    out string operationId) ||
                string.IsNullOrWhiteSpace(operationId))
            {
                return;
            }

            string state =
                nexEvent.Name?.Trim().ToLowerInvariant() switch
                {
                    "region.control.operation.accepted" =>
                        "accepted",
                    "region.control.operation.completed" =>
                        "completed",
                    "region.control.operation.failed" =>
                        "failed",
                    _ =>
                        string.Empty
                };

            if (state.Length == 0)
                return;

            if (!m_Operations.TryGetValue(
                    operationId.Trim(),
                    out OperationRecord record))
            {
                return;
            }

            lock (record.Sync)
            {
                record.State = state;
                record.UpdatedAt =
                    DateTimeOffset.UtcNow;

                if (nexEvent.Data.TryGetValue(
                        "message",
                        out string message))
                {
                    record.Message =
                        message ?? string.Empty;
                }

                foreach (KeyValuePair<string, string> item in nexEvent.Data)
                {
                    if (string.Equals(
                            item.Key,
                            "operation_id",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    record.Details[item.Key] =
                        item.Value ?? string.Empty;
                }
            }
        }

        private static NexRegionOperationSnapshot Snapshot(
            OperationRecord record)
        {
            lock (record.Sync)
            {
                return new NexRegionOperationSnapshot(
                    record.OperationId,
                    record.Operation,
                    record.NodeId,
                    record.RegionId,
                    record.State,
                    record.Message,
                    record.CorrelationId,
                    record.CreatedAt,
                    record.UpdatedAt,
                    new Dictionary<string, string>(
                        record.Details,
                        StringComparer.OrdinalIgnoreCase));
            }
        }

        private void Trim()
        {
            if (m_Operations.Count <= MaximumEntries)
                return;

            foreach (string operationId in
                     m_Operations.Values
                         .OrderBy(x => x.CreatedAt)
                         .Take(
                             m_Operations.Count -
                             MaximumEntries)
                         .Select(x => x.OperationId)
                         .ToArray())
            {
                m_Operations.TryRemove(
                    operationId,
                    out _);
            }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(
                    ref m_Disposed,
                    1) != 0)
            {
                return;
            }

            m_Subscription?.Dispose();
            m_Operations.Clear();
        }

        private sealed class OperationRecord
        {
            public OperationRecord(
                string operationId,
                string operation,
                string nodeId,
                string regionId,
                string correlationId,
                DateTimeOffset createdAt,
                IReadOnlyDictionary<string, string> details)
            {
                OperationId = operationId;
                Operation = operation ?? string.Empty;
                NodeId = nodeId ?? string.Empty;
                RegionId = regionId ?? string.Empty;
                CorrelationId = correlationId ?? string.Empty;
                CreatedAt = createdAt;
                UpdatedAt = createdAt;
                State = "queued";
                Message = string.Empty;
                Details =
                    details == null
                        ? new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(
                            details,
                            StringComparer.OrdinalIgnoreCase);
            }

            public object Sync { get; } = new object();
            public string OperationId { get; }
            public string Operation { get; }
            public string NodeId { get; }
            public string RegionId { get; }
            public string CorrelationId { get; }
            public DateTimeOffset CreatedAt { get; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string State { get; set; }
            public string Message { get; set; }
            public Dictionary<string, string> Details { get; }
        }
    }
}
