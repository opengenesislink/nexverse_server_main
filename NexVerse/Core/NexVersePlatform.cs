// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core
{
    public static class NexVersePlatform
    {
        public const string ProductName = "OpenGenesisLINK";
        public const string MilestoneVersion = "0.9.3.5";
        public const string MilestoneCodename = "";
        public const string MilestoneTitle = "Inventory, OAR/IAR, Job Engine und NexBus";
        public const string UiLanguage = "de-DE";
        public const string ApiVersion = "v1";
        public const string ProtocolVersion = "1";
        public const string NexBusSchemaVersion = "1";

        public static IReadOnlyDictionary<string, string> GetCompatibilityLevels()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api"] = ApiVersion,
                ["protocol"] = ProtocolVersion,
                ["nexbus"] = NexBusSchemaVersion,
                ["hypergrid"] = "HG1.5",
                ["voice"] = "planned",
                ["economy"] = "planned",
                ["lsl"] = "baseline-audit-pending",
                ["firestorm"] = "baseline-validation-pending"
            };
        }
    }
}
