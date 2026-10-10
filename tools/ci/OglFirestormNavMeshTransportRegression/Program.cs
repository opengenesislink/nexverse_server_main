// SPDX-License-Identifier: MPL-2.0
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NexVerse.Core.Pathfinding;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
static bool Reject(Action operation)
{
    try { operation(); return false; }
    catch (ArgumentException) { return true; }
}
static byte[] Deflate(byte[] data, bool gzip)
{
    using MemoryStream output = new();
    using (Stream compressor = gzip
        ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)
        : new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        compressor.Write(data);
    return output.ToArray();
}

// Transport-only fixture. It deliberately is NOT asserted to be a
// Havok-compatible navmesh and MUST NOT be served to Firestorm.
byte[] mock = new byte[12000];
new Random(4711).NextBytes(mock);
byte[] gzip = Deflate(mock, true);
byte[] zlib = Deflate(mock, false);
OglFirestormNavMeshTransport g = new(42, gzip);
OglFirestormNavMeshTransport z = new(42, zlib);
Check(g.Version == 42 && z.Version == 42 &&
    g.ExpandedBytes == mock.Length && z.ExpandedBytes == mock.Length,
    "gzip and zlib Firestorm decompression accepted");

byte first = gzip[0];
gzip[0] = 0;
Check(g.CopyCompressedBytes()[0] == first,
    "published transport is defensively copied");
byte[] published = g.CopyCompressedBytes();
published[0] = 0;
Check(g.CopyCompressedBytes()[0] == first,
    "returned compressed transport is also defensively copied");
Check(g.Sha256.Length == 64 && z.Sha256.Length == 64,
    "transport hash exposes no raw NavMesh data");

Check(Reject(() => new OglFirestormNavMeshTransport(0, zlib)),
    "invalid generation rejected");
Check(Reject(() => new OglFirestormNavMeshTransport(1, null)),
    "missing NavMesh rejected");
Check(Reject(() => new OglFirestormNavMeshTransport(1, new byte[10])),
    "short or invalid NavMesh rejected");
Check(Reject(() => new OglFirestormNavMeshTransport(1,
    Enumerable.Repeat((byte)0x7f, 1024).ToArray())),
    "non-zlib non-gzip binary rejected");
Check(Reject(() => new OglFirestormNavMeshTransport(1,
    zlib[..^9])),
    "truncated compressed transport rejected");
Check(Reject(() => new OglFirestormNavMeshTransport(1,
    Deflate(new byte[OglFirestormNavMeshTransport.MaxExpandedBytes + 1], false))),
    "decompression bomb rejected by expanded-byte limit");
Check(Reject(() => new OglFirestormNavMeshTransport(1,
    new byte[OglFirestormNavMeshTransport.MaxCompressedBytes + 1])),
    "compressed transport cap enforced");
Console.WriteLine("OGL Firestorm NavMesh wire transport regression: OK");
