// SPDX-License-Identifier: MPL-2.0
using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>
    /// OpenGenesisLINK NAVGRAPH wire format 1. This is NOT a Havok binary
    /// navmesh, Recast/Detour polygon mesh, or Firestorm RetrieveNavMeshSrc.
    /// BinaryWriter/Reader are strictly little-endian on .NET.
    ///
    /// Header: ASCII "OGLN", int32 version=1, int32 epoch,
    /// float32 cellMeters, int32 nodeCount, int32 directedArcCount.
    /// Node: int32 x,y,layer; float32 z,maxAgentRadius; byte walkable.
    /// Arc: int32 from,to; byte offMesh.
    /// Trailer: SHA256 of all preceding bytes (32 bytes).
    /// </summary>
    public static class OglOpenNavGraphCodec
    {
        public const int FormatVersion = 1;
        public const int MaxBytes = 12 * 1024 * 1024;
        public const int MaxNodes = 65536;
        public const int MaxArcs = 600000;
        private const int HeaderBytes = 24;
        private const int NodeBytes = 21;
        private const int ArcBytes = 9;
        private const int ChecksumBytes = 32;

        public static byte[] Encode(OglLayeredNavGraph graph, int epoch)
        {
            if (graph == null || epoch < 1 ||
                !graph.TryCaptureCertifiedArcs(out OglOpenNavArc[] arcs))
                throw new ArgumentException(
                    "Only a current physics-certified graph is exportable.");
            if (graph.NodeCount < 1 || graph.NodeCount > MaxNodes ||
                arcs.Length > MaxArcs)
                throw new ArgumentOutOfRangeException(nameof(graph));
            long expected = HeaderBytes + (long)graph.NodeCount * NodeBytes +
                (long)arcs.Length * ArcBytes + ChecksumBytes;
            if (expected > MaxBytes)
                throw new ArgumentOutOfRangeException(nameof(graph));

            using MemoryStream stream = new((int)expected);
            using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(new byte[] { (byte)'O', (byte)'G', (byte)'L', (byte)'N' });
                writer.Write(FormatVersion);
                writer.Write(epoch);
                writer.Write(graph.CellMeters);
                writer.Write(graph.NodeCount);
                writer.Write(arcs.Length);
                for (int i = 0; i < graph.NodeCount; ++i)
                {
                    OglLayerNavNode node = graph.GetNode(i);
                    writer.Write(node.X);
                    writer.Write(node.Y);
                    writer.Write(node.Layer);
                    writer.Write(node.Z);
                    writer.Write(node.MaxAgentRadius);
                    writer.Write((byte)(node.Walkable ? 1 : 0));
                }
                foreach (OglOpenNavArc arc in arcs)
                {
                    writer.Write(arc.From);
                    writer.Write(arc.To);
                    writer.Write((byte)(arc.OffMesh ? 1 : 0));
                }
            }
            byte[] body = stream.ToArray();
            byte[] hash = SHA256.HashData(body);
            stream.Write(hash);
            if (stream.Length != expected)
                throw new InvalidDataException("NavGraph wire length mismatch.");
            return stream.ToArray();
        }

        /// <summary>Validated parser useful for unit tests and other clients.
        /// Never decodes a legacy/unverified mesh into a trusted path.</summary>
        public static bool TryDecode(byte[] packet, out int epoch,
            out float cellMeters, out OglLayerNavNode[] nodes,
            out OglOpenNavArc[] arcs)
        {
            epoch = 0;
            cellMeters = 0;
            nodes = Array.Empty<OglLayerNavNode>();
            arcs = Array.Empty<OglOpenNavArc>();
            if (packet == null || packet.Length < HeaderBytes +
                NodeBytes + ChecksumBytes || packet.Length > MaxBytes)
                return false;
            ReadOnlySpan<byte> signed = packet.AsSpan(0, packet.Length - ChecksumBytes);
            byte[] computed = SHA256.HashData(signed);
            if (!CryptographicOperations.FixedTimeEquals(computed,
                packet.AsSpan(packet.Length - ChecksumBytes)))
                return false;

            try
            {
                using MemoryStream stream = new(packet, false);
                using BinaryReader reader = new(stream);
                if (reader.ReadByte() != 'O' || reader.ReadByte() != 'G' ||
                    reader.ReadByte() != 'L' || reader.ReadByte() != 'N' ||
                    reader.ReadInt32() != FormatVersion)
                    return false;
                int parsedEpoch = reader.ReadInt32();
                float cell = reader.ReadSingle();
                int n = reader.ReadInt32();
                int a = reader.ReadInt32();
                long expected = HeaderBytes + (long)n * NodeBytes +
                    (long)a * ArcBytes + ChecksumBytes;
                if (parsedEpoch < 1 || !float.IsFinite(cell) ||
                    cell < 0.25f || cell > 32f || n < 1 || n > MaxNodes ||
                    a < 0 || a > MaxArcs || expected != packet.Length)
                    return false;

                OglLayerNavNode[] parsedNodes = new OglLayerNavNode[n];
                for (int i = 0; i < n; ++i)
                {
                    int x = reader.ReadInt32();
                    int y = reader.ReadInt32();
                    int layer = reader.ReadInt32();
                    float z = reader.ReadSingle();
                    float radius = reader.ReadSingle();
                    byte walkable = reader.ReadByte();
                    if (x < 0 || x > 4095 || y < 0 || y > 4095 ||
                        layer < 0 || layer > 1023 || !float.IsFinite(z) ||
                        !float.IsFinite(radius) || radius < 0f ||
                        radius > 5f || walkable > 1)
                        return false;
                    parsedNodes[i] = new OglLayerNavNode(
                        x, y, layer, z, radius, walkable == 1);
                }
                OglOpenNavArc[] parsedArcs = new OglOpenNavArc[a];
                OglOpenNavArc previous = default;
                for (int i = 0; i < a; ++i)
                {
                    int from = reader.ReadInt32();
                    int to = reader.ReadInt32();
                    byte portal = reader.ReadByte();
                    if (from < 0 || from >= n || to < 0 || to >= n ||
                        from == to || portal > 1)
                        return false;
                    if (i > 0 && (from < previous.From ||
                        (from == previous.From && to < previous.To) ||
                        (from == previous.From && to == previous.To &&
                         portal < (previous.OffMesh ? 1 : 0))))
                        return false;
                    parsedArcs[i] = previous = new OglOpenNavArc(
                        from, to, portal == 1);
                }
                epoch = parsedEpoch;
                cellMeters = cell;
                nodes = parsedNodes;
                arcs = parsedArcs;
                return true;
            }
            catch (EndOfStreamException) { return false; }
            catch (IOException) { return false; }
        }
    }
}
