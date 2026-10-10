// SPDX-License-Identifier: MPL-2.0
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace NexVerse.Core.Pathfinding
{
    /// <summary>
    /// Bounded Firestorm RetrieveNavMeshSrc wire transport. Accepts zlib or gzip
    /// compression as Firestorm's unzip_llsdNavMesh does. This class validates
    /// transport integrity, NOT the proprietary/implementation-specific
    /// contents of the decompressed NavMesh; only a trusted source that has
    /// independently verified viewer compatibility may publish it.
    /// </summary>
    public sealed class OglFirestormNavMeshTransport
    {
        public const int MaxCompressedBytes = 8 * 1024 * 1024;
        public const int MaxExpandedBytes = 32 * 1024 * 1024;
        private readonly byte[] m_Compressed;
        public int Version { get; }
        public int ExpandedBytes { get; }
        public string Sha256 { get; }

        public OglFirestormNavMeshTransport(int version, byte[] compressed)
        {
            if (version <= 0)
                throw new ArgumentOutOfRangeException(nameof(version));
            if (compressed == null || compressed.Length < 20 ||
                compressed.Length > MaxCompressedBytes)
                throw new ArgumentException("NavMesh transport is missing or oversized.", nameof(compressed));

            // Copy before checking to avoid a caller mutating the buffer after
            // validation and before the capability serializes it.
            m_Compressed = (byte[])compressed.Clone();
            bool isGzip = m_Compressed[0] == 0x1f && m_Compressed[1] == 0x8b;
            bool isZlib = (m_Compressed[0] & 0x0f) == 8 &&
                (((m_Compressed[0] << 8) | m_Compressed[1]) % 31) == 0;
            if (!isGzip && !isZlib)
                throw new ArgumentException("NavMesh must use gzip or zlib compression.", nameof(compressed));

            int count = 0;
            try
            {
                using MemoryStream stream = new(m_Compressed, writable: false);
                using Stream inflater = isGzip
                    ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false)
                    : new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: false);
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (count > MaxExpandedBytes - read)
                        throw new ArgumentException(
                            "NavMesh decompression budget exceeded.", nameof(compressed));
                    count += read;
                }
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException(
                    "NavMesh compressed transport is corrupt.", nameof(compressed), e);
            }
            if (count < 16)
                throw new ArgumentException("NavMesh transport has no usable content.", nameof(compressed));
            Version = version;
            ExpandedBytes = count;
            Sha256 = Convert.ToHexString(SHA256.HashData(m_Compressed));
        }

        public byte[] CopyCompressedBytes() => (byte[])m_Compressed.Clone();
    }
}
