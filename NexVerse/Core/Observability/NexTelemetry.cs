// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

namespace NexVerse.Core.Observability
{
    public static class NexTelemetry
    {
        public const string ActivitySourceName = "NexVerse";
        public static ActivitySource ActivitySource { get; } =
            new ActivitySource(ActivitySourceName, NexVersePlatform.ApiVersion);
    }
}
