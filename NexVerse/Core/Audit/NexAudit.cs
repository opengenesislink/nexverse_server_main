// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Audit
{
    public sealed class NexAuditEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
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
        {
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
