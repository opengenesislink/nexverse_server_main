// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NexVerse.Core.Messaging
{
    public sealed class DistributedNexEventBus : INexEventBus, IDisposable
    {
        private const int DefaultDeduplicationWindow = 10000;

        private readonly InMemoryNexEventBus m_Local = new InMemoryNexEventBus();
        private readonly INexEventTransport m_Transport;
        private readonly ConcurrentDictionary<Guid, byte> m_Seen =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentQueue<Guid> m_SeenOrder =
            new ConcurrentQueue<Guid>();
        private readonly int m_DeduplicationWindow;
        private int m_Disposed;

        public DistributedNexEventBus(
            INexEventTransport transport,
            int deduplicationWindow = DefaultDeduplicationWindow)
        {
            m_Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_DeduplicationWindow = Math.Max(128, deduplicationWindow);
        }

        public IDisposable Subscribe(string eventName, Action<NexEvent> handler)
        {
            ThrowIfDisposed();
            return m_Local.Subscribe(eventName, handler);
        }

        public void Publish(NexEvent nexEvent)
        {
            ThrowIfDisposed();

            if (nexEvent == null)
                throw new ArgumentNullException(nameof(nexEvent));

            if (!TryMarkSeen(nexEvent.EventId))
                return;

            m_Local.Publish(nexEvent);
            m_Transport.Send(nexEvent);
        }

        public bool Receive(NexEvent nexEvent)
        {
            ThrowIfDisposed();

            if (nexEvent == null)
                throw new ArgumentNullException(nameof(nexEvent));

            if (!TryMarkSeen(nexEvent.EventId))
                return false;

            m_Local.Publish(nexEvent);

            // Re-relay accepted remote events. Peers receiving the same EventId
            // suppress it, allowing hub or mesh topologies without event loops.
            m_Transport.Send(nexEvent);
            return true;
        }

        private bool TryMarkSeen(Guid eventId)
        {
            if (eventId == Guid.Empty || !m_Seen.TryAdd(eventId, 0))
                return false;

            m_SeenOrder.Enqueue(eventId);
            TrimSeen();
            return true;
        }

        private void TrimSeen()
        {
            while (m_Seen.Count > m_DeduplicationWindow &&
                   m_SeenOrder.TryDequeue(out Guid oldId))
            {
                m_Seen.TryRemove(oldId, out _);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref m_Disposed) != 0)
                throw new ObjectDisposedException(nameof(DistributedNexEventBus));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0)
                return;

            m_Transport.Dispose();
        }
    }
}
