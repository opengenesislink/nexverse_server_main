// SPDX-License-Identifier: MPL-2.0

using System;

namespace NexVerse.Core.Messaging
{
    public interface INexEventBus
    {
        IDisposable Subscribe(string eventName, Action<NexEvent> handler);
        void Publish(NexEvent nexEvent);
    }

    public interface INexEventTransport : IDisposable
    {
        void Send(NexEvent nexEvent);
    }
}
