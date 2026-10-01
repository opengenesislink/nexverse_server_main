// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Audit
{
    public sealed class NexAuditEvent
    {
        public Guid EventId { get; }
        public DateTimeOffset Timestamp { get; }
        public string Actor { get; }
        public string Action { get; }
        public string Resource { get; }
        public string CorrelationId { get; }
        public IReadOnlyDictionary<string, string> Details { get; }

        public NexAuditEvent(
            string actor,
            string action,
            string resource,
            string correlationId = null,
            IReadOnlyDictionary<string, string> details = null)
            : this(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                actor,
                action,
                resource,
                correlationId,
                details)
        {
        }

        public NexAuditEvent(
            Guid eventId,
            DateTimeOffset timestamp,
            string actor,
            string action,
            string resource,
            string correlationId = null,
            IReadOnlyDictionary<string, string> details = null)
        {
            if (eventId == Guid.Empty)
                throw new ArgumentException("Audit event ID is required.", nameof(eventId));

            EventId = eventId;
            Timestamp = timestamp;
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor;
            Action = action ?? string.Empty;
            Resource = resource ?? string.Empty;
            CorrelationId = correlationId ?? string.Empty;
            Details = details ?? new Dictionary<string, string>();
        }
    }

    public interface INexAuditSink
    {
        void Record(NexAuditEvent auditEvent);
    }

    public sealed class NexAuditQueryResult
    {
        public IReadOnlyList<NexAuditEvent> Events { get; }
        public bool HasMore { get; }

        public NexAuditQueryResult(
            IReadOnlyList<NexAuditEvent> events,
            bool hasMore)
        {
            Events = events ?? Array.Empty<NexAuditEvent>();
            HasMore = hasMore;
        }
    }

    public interface INexAuditStore : INexAuditSink
    {
        NexAuditQueryResult Query(
            string resource,
            string actor,
            string action,
            int limit,
            int offset);
    }

    public sealed class NullNexAuditSink : INexAuditSink
    {
        public static NullNexAuditSink Instance { get; } = new NullNexAuditSink();

        private NullNexAuditSink()
        {
        }

        public void Record(NexAuditEvent auditEvent)
        {
        }
    }
}
