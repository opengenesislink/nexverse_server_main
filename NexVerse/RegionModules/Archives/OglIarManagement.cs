// OpenGenesisLINK - IAR management foundation
// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NexVerse.RegionModules.Archives
{
    public sealed class OglIarInspection
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public bool Valid { get; set; }
        public bool HasControlFile { get; set; }
        public int Entries { get; set; }
        public int InventoryEntries { get; set; }
        public int Assets { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public static class OglIarInspector
    {
        public static OglIarInspection Inspect(string archivePath)
        {
            OglIarInspection result = new() { Path = archivePath ?? string.Empty };
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                result.Error = "IAR-Datei wurde nicht gefunden.";
                return result;
            }

            try
            {
                FileInfo info = new(archivePath);
                result.SizeBytes = info.Length;
                using (FileStream hashStream = File.OpenRead(archivePath))
                using (SHA256 sha = SHA256.Create())
                    result.Sha256 = Convert.ToHexString(sha.ComputeHash(hashStream)).ToLowerInvariant();

                using FileStream input = File.OpenRead(archivePath);
                using GZipStream gzip = new(input, CompressionMode.Decompress);
                using BinaryReader tar = new(gzip, Encoding.UTF8, leaveOpen: true);
                while (OglTarReader.TryReadEntry(tar, out string path, out long size, out byte type))
                {
                    if (type != (byte)'5')
                    {
                        result.Entries++;
                        if (path.Equals("archive.xml", StringComparison.Ordinal))
                            result.HasControlFile = size > 0;
                        else if (path.StartsWith("inventory/", StringComparison.Ordinal))
                            result.InventoryEntries++;
                        else if (path.StartsWith("assets/", StringComparison.Ordinal))
                            result.Assets++;
                    }
                    OglTarReader.SkipPayload(tar, size);
                }

                result.Valid = result.HasControlFile && result.InventoryEntries > 0;
                if (!result.Valid)
                    result.Error = "Archiv enthaelt keine gueltige IAR-Struktur.";
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is FormatException)
            {
                result.Error = "IAR konnte nicht gelesen werden: " + e.Message;
            }

            return result;
        }
    }

    internal static class OglTarReader
    {
        public static bool TryReadEntry(BinaryReader reader, out string path, out long size, out byte type)
        {
            path = string.Empty; size = 0; type = 0;
            byte[] header = reader.ReadBytes(512);
            if (header.Length == 0) return false;
            if (header.Length != 512) throw new InvalidDataException("Unvollstaendiger TAR-Header.");
            if (header[0] == 0) return false;

            path = ReadText(header, 0, 100);
            string prefix = ReadText(header, 345, 155);
            if (!string.IsNullOrEmpty(prefix)) path = prefix + "/" + path;
            string octalSize = ReadText(header, 124, 12).Trim();
            if (!string.IsNullOrEmpty(octalSize)) size = Convert.ToInt64(octalSize, 8);
            type = header[156];
            return true;
        }

        private static string ReadText(byte[] buffer, int offset, int length)
        {
            string value = Encoding.ASCII.GetString(buffer, offset, length);
            int nul = value.IndexOf('\0');
            return (nul >= 0 ? value.Substring(0, nul) : value).Trim();
        }

        public static void SkipPayload(BinaryReader reader, long size)
        {
            if (size < 0) throw new InvalidDataException("Ungueltige TAR-Eintragsgroesse.");
            long padded = ((size + 511L) / 512L) * 512L;
            while (padded > 0)
            {
                int take = (int)Math.Min(8192, padded);
                if (reader.ReadBytes(take).Length != take)
                    throw new EndOfStreamException("IAR/TAR endet innerhalb eines Eintrags.");
                padded -= take;
            }
        }
    }

    public sealed class OglIarStoragePolicy
    {
        public string RootDirectory { get; }
        public long MaximumArchiveBytes { get; }

        public OglIarStoragePolicy(string rootDirectory, long maximumArchiveBytes)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("IAR-Speicherverzeichnis darf nicht leer sein.", nameof(rootDirectory));
            if (maximumArchiveBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArchiveBytes));
            RootDirectory = System.IO.Path.GetFullPath(rootDirectory);
            MaximumArchiveBytes = maximumArchiveBytes;
        }

        public string ResolveArchivePath(string fileName)
        {
            string safeName = System.IO.Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeName) || !safeName.EndsWith(".iar", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Nur .iar-Dateien sind im verwalteten Archivspeicher erlaubt.", nameof(fileName));
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootDirectory, safeName));
            string prefix = RootDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("IAR-Pfad verlaesst den verwalteten Speicherbereich.");
            return candidate;
        }

        public bool AcceptsExistingArchive(string path, out string reason)
        {
            reason = string.Empty;
            if (!File.Exists(path)) { reason = "IAR-Datei wurde nicht gefunden."; return false; }
            long size = new FileInfo(path).Length;
            if (size <= 0 || size > MaximumArchiveBytes) { reason = "IAR-Dateigroesse liegt ausserhalb der erlaubten Speicher-Policy."; return false; }
            return true;
        }
    }
}
