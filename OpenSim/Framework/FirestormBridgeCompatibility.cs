/*
 * OpenGenesisLINK Firestorm compatibility helpers.
 */

using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    public static class FirestormBridgeCompatibility
    {
        public const string RootFolderName = "#Firestorm";
        public const string BridgeFolderName = "#LSL Bridge";
        public const string DefaultBridgeVersion = "2.29";

        public static readonly UUID BootstrapAssetId =
            new("9d31c5bf-0d7b-4a6b-b9df-f2c8a6e5a229");

        public static readonly UUID BootstrapObjectId =
            new("c4f3a692-92ac-4f47-a0c6-978af5e9b627");

        public static string GetBridgeItemName(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                version = DefaultBridgeVersion;

            return $"#Firestorm LSL Bridge v{version.Trim()}";
        }

        public static bool IsFirestormViewer(string clientVersion, string channel)
        {
            return
                (!string.IsNullOrWhiteSpace(clientVersion) &&
                 clientVersion.Contains("Firestorm", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(channel) &&
                 channel.Contains("Firestorm", StringComparison.OrdinalIgnoreCase));
        }
    }
}
