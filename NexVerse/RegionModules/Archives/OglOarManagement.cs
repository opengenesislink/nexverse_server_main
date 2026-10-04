// OpenGenesisLINK - OAR management foundation
// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using Ionic.Zlib;
using System.Text;
using CompressionMode = Ionic.Zlib.CompressionMode;

namespace NexVerse.RegionModules.Archives
{
    public sealed class OglOarInspection
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public bool Valid { get; set; }
        public bool HasControlFile { get; set; }
        public int Entries { get; set; }
        public int Assets { get; set; }
        public int Objects { get; set; }
        public int Terrains { get; set; }
        public int Settings { get; set; }
        public int Parcels { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Read-only validation/dry-run metadata pass. It never mutates a scene or asset service.
    /// Actual imports/exports continue to use IRegionArchiverModule as the authoritative path.
    /// IRegionArchiverModule is intentionally not reimplemented here.
    /// </summary>
    public static class OglOarInspector
    {
        public static OglOarInspection Inspect(string archivePath)
        {
            OglOarInspection result = new() { Path = archivePath ?? string.Empty };
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                result.Error = "OAR-Datei wurde nicht gefunden.";
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
                TarArchiveReader reader = new(gzip);

                while (reader.ReadEntry(out string path, out TarArchiveReader.TarEntryType type) is byte[] data)
                {
                    if (type == TarArchiveReader.TarEntryType.TYPE_DIRECTORY)
                        continue;

                    result.Entries++;
                    if (path.Equals(ArchiveConstants.CONTROL_FILE_PATH, StringComparison.Ordinal))
                        result.HasControlFile = data.Length > 0;
                    else if (path.StartsWith(ArchiveConstants.ASSETS_PATH, StringComparison.Ordinal))
                        result.Assets++;
                    else if (path.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal))
                        result.Objects++;
                    else if (path.StartsWith(ArchiveConstants.TERRAINS_PATH, StringComparison.Ordinal))
                        result.Terrains++;
                    else if (path.StartsWith(ArchiveConstants.SETTINGS_PATH, StringComparison.Ordinal))
                        result.Settings++;
                    else if (path.StartsWith(ArchiveConstants.LANDDATA_PATH, StringComparison.Ordinal))
                        result.Parcels++;
                }

                result.Valid = result.HasControlFile && result.Entries > 0;
                if (!result.Valid)
                    result.Error = "Archiv enthaelt keine gueltige OAR-Steuerdatei.";
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is FormatException)
            {
                result.Error = "OAR konnte nicht gelesen werden: " + e.Message;
            }

            return result;
        }
    }

        private static bool TryReadTarEntry(BinaryReader reader, out string path, out long size, out byte type)
        {
            path = string.Empty;
            size = 0;
            type = 0;
            byte[] header = reader.ReadBytes(512);
            if (header.Length == 0)
                return false;
            if (header.Length != 512)
                throw new InvalidDataException("Unvollstaendiger TAR-Header.");
            if (header[0] == 0)
                return false;

            path = ReadTarText(header, 0, 100);
            string prefix = ReadTarText(header, 345, 155);
            if (!string.IsNullOrEmpty(prefix))
                path = prefix + "/" + path;

            string octalSize = ReadTarText(header, 124, 12).Trim();
            if (!string.IsNullOrEmpty(octalSize))
                size = Convert.ToInt64(octalSize, 8);
            type = header[156];
            return true;
        }

        private static string ReadTarText(byte[] buffer, int offset, int length)
        {
            string value = Encoding.ASCII.GetString(buffer, offset, length);
            int nul = value.IndexOf('\0');
            return (nul >= 0 ? value.Substring(0, nul) : value).Trim();
        }

        private static void SkipTarPayload(BinaryReader reader, long size)
        {
            if (size < 0)
                throw new InvalidDataException("Ungueltige TAR-Eintragsgroesse.");
            long padded = ((size + 511L) / 512L) * 512L;
            const int chunkSize = 8192;
            while (padded > 0)
            {
                int take = (int)Math.Min(chunkSize, padded);
                byte[] skipped = reader.ReadBytes(take);
                if (skipped.Length != take)
                    throw new EndOfStreamException("OAR/TAR endet innerhalb eines Eintrags.");
                padded -= take;
            }
        }

    public sealed class OglOarStoragePolicy
    {
        public string RootDirectory { get; }
        public long MaximumArchiveBytes { get; }

        public OglOarStoragePolicy(string rootDirectory, long maximumArchiveBytes)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("OAR-Speicherverzeichnis darf nicht leer sein.", nameof(rootDirectory));
            if (maximumArchiveBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumArchiveBytes));

            RootDirectory = Path.GetFullPath(rootDirectory);
            MaximumArchiveBytes = maximumArchiveBytes;
        }

        public string ResolveArchivePath(string fileName)
        {
            string safeName = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeName) || !safeName.EndsWith(".oar", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Nur .oar-Dateien sind im verwalteten Archivspeicher erlaubt.", nameof(fileName));

            string candidate = Path.GetFullPath(Path.Combine(RootDirectory, safeName));
            string prefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("OAR-Pfad verlaesst den verwalteten Speicherbereich.");
            return candidate;
        }

        public bool AcceptsExistingArchive(string path, out string reason)
        {
            reason = string.Empty;
            if (!File.Exists(path))
            {
                reason = "OAR-Datei wurde nicht gefunden.";
                return false;
            }

            long size = new FileInfo(path).Length;
            if (size <= 0 || size > MaximumArchiveBytes)
            {
                reason = "OAR-Dateigroesse liegt ausserhalb der erlaubten Speicher-Policy.";
                return false;
            }

            return true;
        }
    }
}
