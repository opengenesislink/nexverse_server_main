// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NexVerse.Core.Messaging;

namespace NexVerse.Core.ControlPlane
{
    public static class NexNodeCommandProtocol
    {
        public const string RequestEvent = "node.command.request";
        public const string ResultEvent = "node.command.result";
        public const string PingAction = "ping";

        public const string PendingState = "pending";
        public const string CompletedState = "completed";
        public const string RejectedState = "rejected";
        public const string ExpiredState = "expired";
    }

    public sealed class NexNodeCommandSnapshot
    {
        public NexNodeCommandSnapshot(
            Guid commandId,
            string nodeId,
            string action,
            string requestedBy,
            string correlationId,
            string state,
            string message,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            DateTimeOffset expiresAt)
        {
            CommandId = commandId;
            NodeId = nodeId ?? string.Empty;
            Action = action ?? string.Empty;
            RequestedBy = requestedBy ?? string.Empty;
            CorrelationId = correlationId ?? string.Empty;
            State = state ?? NexNodeCommandProtocol.PendingState;
            Message = message ?? string.Empty;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            ExpiresAt = expiresAt;
        }

        public Guid CommandId { get; }
        public string NodeId { get; }
        public string Action { get; }
        public string RequestedBy { get; }
        public string CorrelationId { get; }
        public string State { get; }
        public string Message { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
    }

    /// <summary>
    /// Tracks directed simulator command requests and asynchronous NodeAgent results.
    /// The first supported action is a non-mutating ping used to verify the command
    /// path before lifecycle mutations are enabled.
    /// </summary>
    public sealed class NexNodeCommandTracker : IDisposable
    {
        private readonly INexEventBus m_EventBus;
        private readonly ConcurrentDictionary<Guid, CommandRecord> m_Commands =
            new ConcurrentDictionary<Guid, CommandRecord>();
        private readonly ConcurrentQueue<Guid> m_Order =
            new ConcurrentQueue<Guid>();
        private readonly IDisposable m_ResultSubscription;
        private readonly TimeSpan m_CommandTimeout;
        private readonly int m_HistoryLimit;
        private int m_Disposed;

        public NexNodeCommandTracker(
            INexEventBus eventBus,
            int commandTimeoutSeconds = 15,
            int historyLimit = 1024)
        {
            m_EventBus =
                eventBus ??
                throw new ArgumentNullException(nameof(eventBus));
            m_CommandTimeout =
                TimeSpan.FromSeconds(
                    Math.Max(5, commandTimeoutSeconds));
            m_HistoryLimit =
                Math.Max(128, historyLimit);
            m_ResultSubscription =
                eventBus.Subscribe(
                    NexNodeCommandProtocol.ResultEvent,
                    HandleResult);
        }

        public int CommandTimeoutSeconds =>
            (int)m_CommandTimeout.TotalSeconds;

        public NexNodeCommandSnapshot IssuePing(
            string nodeId,
            string requestedBy,
            string correlationId)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException(
                    "Target node ID is required.",
                    nameof(nodeId));

            DateTimeOffset now =
                DateTimeOffset.UtcNow;
            Guid commandId =
                Guid.NewGuid();

            CommandRecord record =
                new CommandRecord(
                    commandId,
                    nodeId.Trim(),
                    NexNodeCommandProtocol.PingAction,
                    requestedBy,
                    correlationId,
                    now,
                    now.Add(m_CommandTimeout));

            if (!m_Commands.TryAdd(
                    commandId,
                    record))
            {
                throw new InvalidOperationException(
                    "Unable to reserve a unique node command ID.");
            }

            m_Order.Enqueue(commandId);
            TrimHistory();

            IReadOnlyDictionary<string, string> data =
                new Dictionary<string, string>
                {
                    ["command_id"] =
                        commandId.ToString(),
                    ["target_node_id"] =
                        record.NodeId,
                    ["action"] =
                        record.Action,
                    ["requested_by"] =
                        record.RequestedBy,
                    ["expires_at_unix"] =
                        record.ExpiresAt
                            .ToUnixTimeSeconds()
                            .ToString(
                                CultureInfo.InvariantCulture)
                };

            try
            {
                m_EventBus.Publish(
                    new NexEvent(
                        NexNodeCommandProtocol.RequestEvent,
                        "nexverse.robust",
                        data,
                        record.CorrelationId));
            }
            catch
            {
                m_Commands.TryRemove(
                    commandId,
                    out _);
                throw;
            }

            return Snapshot(
                record,
                now);
        }

        public NexNodeCommandSnapshot Get(
            string commandId)
        {
            if (!Guid.TryParse(
                    commandId,
                    out Guid parsed) ||
                !m_Commands.TryGetValue(
                    parsed,
                    out CommandRecord record))
            {
                return null;
            }

            return Snapshot(
                record,
                DateTimeOffset.UtcNow);
        }

        public IReadOnlyList<NexNodeCommandSnapshot> List(
            int limit = 100)
        {
            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            return m_Commands.Values
                .Select(record =>
                    Snapshot(record, now))
                .OrderByDescending(snapshot =>
                    snapshot.CreatedAt)
                .Take(Math.Max(1, Math.Min(500, limit)))
                .ToArray();
        }

        private void HandleResult(
            NexEvent nexEvent)
        {
            if (nexEvent?.Data == null ||
                !nexEvent.Data.TryGetValue(
                    "command_id",
                    out string commandIdRaw) ||
                !Guid.TryParse(
                    commandIdRaw,
                    out Guid commandId) ||
                !m_Commands.TryGetValue(
                    commandId,
                    out CommandRecord record))
            {
                return;
            }

            string nodeId =
                Data(
                    nexEvent,
                    "node_id");
            string action =
                Data(
                    nexEvent,
                    "action");
            string state =
                Data(
                    nexEvent,
                    "state");
            string message =
                Data(
                    nexEvent,
                    "message");

            if (!string.Equals(
                    nodeId,
                    record.NodeId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    action,
                    record.Action,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(
                    state,
                    NexNodeCommandProtocol.CompletedState,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    state,
                    NexNodeCommandProtocol.RejectedState,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (record.Sync)
            {
                if (!string.Equals(
                        record.State,
                        NexNodeCommandProtocol.PendingState,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                record.State =
                    state.ToLowerInvariant();
                record.Message =
                    message;
                record.UpdatedAt =
                    DateTimeOffset.UtcNow;
            }
        }

        private NexNodeCommandSnapshot Snapshot(
            CommandRecord record,
            DateTimeOffset now)
        {
            lock (record.Sync)
            {
                if (string.Equals(
                        record.State,
                        NexNodeCommandProtocol.PendingState,
                        StringComparison.OrdinalIgnoreCase) &&
                    now > record.ExpiresAt)
                {
                    record.State =
                        NexNodeCommandProtocol.ExpiredState;
                    record.Message =
                        "No NodeAgent result was received before the command deadline.";
                    record.UpdatedAt =
                        now;
                }

                return new NexNodeCommandSnapshot(
                    record.CommandId,
                    record.NodeId,
                    record.Action,
                    record.RequestedBy,
                    record.CorrelationId,
                    record.State,
                    record.Message,
                    record.CreatedAt,
                    record.UpdatedAt,
                    record.ExpiresAt);
            }
        }

        private void TrimHistory()
        {
            while (m_Commands.Count >
                   m_HistoryLimit &&
                   m_Order.TryDequeue(
                       out Guid oldest))
            {
                m_Commands.TryRemove(
                    oldest,
                    out _);
            }
        }

        private static string Data(
            NexEvent nexEvent,
            string key)
        {
            if (nexEvent?.Data == null ||
                !nexEvent.Data.TryGetValue(
                    key,
                    out string value) ||
                value == null)
            {
                return string.Empty;
            }

            return value.Trim();
        }

        private void ThrowIfDisposed()
        {
            if (System.Threading.Volatile.Read(
                    ref m_Disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(NexNodeCommandTracker));
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

            m_ResultSubscription?.Dispose();
            m_Commands.Clear();
        }

        private sealed class CommandRecord
        {
            public CommandRecord(
                Guid commandId,
                string nodeId,
                string action,
                string requestedBy,
                string correlationId,
                DateTimeOffset createdAt,
                DateTimeOffset expiresAt)
            {
                CommandId = commandId;
                NodeId = nodeId ?? string.Empty;
                Action = action ?? string.Empty;
                RequestedBy = requestedBy ?? string.Empty;
                CorrelationId = correlationId ?? string.Empty;
                State =
                    NexNodeCommandProtocol.PendingState;
                CreatedAt = createdAt;
                UpdatedAt = createdAt;
                ExpiresAt = expiresAt;
            }

            public object Sync { get; } =
                new object();
            public Guid CommandId { get; }
            public string NodeId { get; }
            public string Action { get; }
            public string RequestedBy { get; }
            public string CorrelationId { get; }
            public string State;
            public string Message = string.Empty;
            public DateTimeOffset CreatedAt;
            public DateTimeOffset UpdatedAt;
            public DateTimeOffset ExpiresAt;
        }
    }
}
