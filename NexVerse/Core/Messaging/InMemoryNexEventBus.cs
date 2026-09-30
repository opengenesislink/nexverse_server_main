// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NexVerse.Core.Messaging
{
    public sealed class InMemoryNexEventBus : INexEventBus
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<NexEvent>>> m_Handlers =
            new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<NexEvent>>>(StringComparer.OrdinalIgnoreCase);

        public IDisposable Subscribe(string eventName, Action<NexEvent> handler)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("Event name is required.", nameof(eventName));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Guid id = Guid.NewGuid();
            ConcurrentDictionary<Guid, Action<NexEvent>> bucket =
                m_Handlers.GetOrAdd(eventName, _ => new ConcurrentDictionary<Guid, Action<NexEvent>>());

            bucket[id] = handler;
            return new Subscription(() => bucket.TryRemove(id, out _));
        }

        public void Publish(NexEvent nexEvent)
        {
            if (nexEvent == null)
                throw new ArgumentNullException(nameof(nexEvent));

            foreach (Action<NexEvent> handler in GetHandlers(nexEvent.Name))
                handler(nexEvent);

            foreach (Action<NexEvent> handler in GetHandlers("*"))
                handler(nexEvent);
        }

        private IEnumerable<Action<NexEvent>> GetHandlers(string eventName)
        {
            if (!m_Handlers.TryGetValue(eventName, out ConcurrentDictionary<Guid, Action<NexEvent>> bucket))
                return Enumerable.Empty<Action<NexEvent>>();

            return bucket.Values.ToArray();
        }

        private sealed class Subscription : IDisposable
        {
            private Action m_Dispose;

            public Subscription(Action dispose)
            {
                m_Dispose = dispose;
            }

            public void Dispose()
            {
                Action dispose = m_Dispose;
                if (dispose == null)
                    return;

                m_Dispose = null;
                dispose();
            }
        }
    }
}
