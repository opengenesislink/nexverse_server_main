/*
 * NexVerse LunaTexture diagnostic contract.
 * Exposes bounded, read-only texture recovery diagnostics to simulator-side
 * management code without coupling consumers to Warp3D internals.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces
{
    public sealed class LunaTextureDiagnosticInfo
    {
        public UUID TextureID { get; set; }
        public string Classification { get; set; }
        public string PrimName { get; set; }
        public string Position { get; set; }
        public string Reason { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int Count { get; set; }
    }

    public interface ILunaTextureDiagnostics
    {
        IReadOnlyList<LunaTextureDiagnosticInfo> GetDiagnostics();
        bool TryGetDiagnostic(UUID textureID, out LunaTextureDiagnosticInfo diagnostic);
        bool RetryTexture(UUID textureID, out string result);
        void ClearDiagnostics();
    }
}
