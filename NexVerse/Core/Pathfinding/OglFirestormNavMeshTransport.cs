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
            // The .NET inflater may accept truncated final trailers on some
            // runtime versions. Verify both formats' integrity checksums
            // ourselves rather than trusting a successful Read() alone.
            ulong adlerA = 1, adlerB = 0;
            uint crc = 0xffffffffu;
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
                    for (int i = 0; i < read; ++i)
                    {
                        byte value = buffer[i];
                        adlerA += value;
                        adlerB += adlerA;
                        crc = s_Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
                    }
                    adlerA %= 65521u;
                    adlerB %= 65521u;
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
            if (isGzip)
            {
                // RFC 1952: footer CRC32 (LE), ISIZE (LE).
                uint expectedCrc = ReadLittleUInt32(m_Compressed, m_Compressed.Length - 8);
                uint expectedSize = ReadLittleUInt32(m_Compressed, m_Compressed.Length - 4);
                if ((crc ^ 0xffffffffu) != expectedCrc || expectedSize != (uint)count)
                    throw new ArgumentException(
                        "NavMesh gzip CRC or length mismatch.", nameof(compressed));
            }
            else
            {
                // RFC 1950: Adler-32 checksum (BE).
                uint expectedAdler = ReadBigUInt32(m_Compressed, m_Compressed.Length - 4);
                uint actualAdler = (uint)((adlerB << 16) | adlerA);
                if (actualAdler != expectedAdler)
                    throw new ArgumentException(
                        "NavMesh zlib Adler-32 mismatch.", nameof(compressed));
            }
            Version = version;
            ExpandedBytes = count;
            Sha256 = Convert.ToHexString(SHA256.HashData(m_Compressed));
        }

        private static uint ReadLittleUInt32(byte[] buffer, int offset) =>
            (uint)buffer[offset] | ((uint)buffer[offset + 1] << 8) |
            ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);

        private static uint ReadBigUInt32(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
            ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        private static readonly uint[] s_Crc32Table = MakeCrc32Table();
        private static uint[] MakeCrc32Table()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; ++i)
            {
                uint value = i;
                for (int bit = 0; bit < 8; ++bit)
                    value = (value & 1) != 0
                        ? (value >> 1) ^ 0xedb88320u : value >> 1;
                table[i] = value;
            }
            return table;
        }

        public byte[] CopyCompressedBytes() => (byte[])m_Compressed.Clone();
    }
}
