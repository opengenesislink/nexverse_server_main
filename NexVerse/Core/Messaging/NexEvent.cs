// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Messaging
{
    public sealed class NexEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
        public string Name { get; }
        public string Source { get; }
        public string CorrelationId { get; }
        public IReadOnlyDictionary<string, string> Data { get; }

        public NexEvent(
            string name,
            string source,
            IReadOnlyDictionary<string, string> data = null,
            string correlationId = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("NexBus event name is required.", nameof(name));

            Name = name;
            Source = string.IsNullOrWhiteSpace(source) ? NexVersePlatform.ProductName : source;
            Data = data ?? new Dictionary<string, string>();
            CorrelationId = correlationId ?? string.Empty;
        }
    }
}
