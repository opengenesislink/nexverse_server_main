// SPDX-License-Identifier: MPL-2.0
using System;

namespace NexVerse.Core.Voice
{
    /// <summary>
    /// Read-only OGLVoice provider discovery status exposed to modules inside
    /// a simulator. Does not indicate whether actual WebRTC voice is ready.
    /// </summary>
    public interface IOglVoiceProviderLookup
    {
        bool DiscoveryEnabled { get; }
        OglVoiceProviderDescriptor CurrentProvider { get; }
        DateTimeOffset LastRefreshUtc { get; }
    }
}
