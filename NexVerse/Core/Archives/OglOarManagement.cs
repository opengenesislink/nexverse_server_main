// OpenGenesisLINK - OAR management foundation
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using OpenSim.Framework.Serialization;
using Ionic.Zlib;
using CompressionMode = Ionic.Zlib.CompressionMode;

namespace NexVerse.Core.Archives
{
    public sealed class OglOarInspection
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public bool Valid { get; set; }
        public bool HasControlFile { get; set; }
        public int Entries { get; set; }
        public int Assets { get; set; }
        public int Objects { get; set; }
        public int Terrains { get; set; }
        public int Settings { get; set; }
        public int Parcels { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Non-mutating OAR inspection used before import and by future job/API layers.
    /// This deliberately does not duplicate the authoritative region archiver.
    /// </summary>
    public static class OglOarInspector
    {
        public static OglOarInspection Inspect(string archivePath)
        {
            OglOarInspection result = new OglOarInspection { Path = archivePath ?? string.Empty };

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                result.Error = "OAR-Datei wurde nicht gefunden.";
                return result;
            }

            try
            {
                FileInfo info = new FileInfo(archivePath);
                result.SizeBytes = info.Length;
                using (FileStream hashStream = File.OpenRead(archivePath))
                using (SHA256 sha = SHA256.Create())
                    result.Sha256 = Convert.ToHexString(sha.ComputeHash(hashStream)).ToLowerInvariant();

                using FileStream input = File.OpenRead(archivePath);
                using GZipStream gzip = new GZipStream(input, CompressionMode.Decompress);
                TarArchiveReader reader = new TarArchiveReader(gzip);

                byte[] data;
                while ((data = reader.ReadEntry(out string path, out TarArchiveReader.TarEntryType type)) != null)
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
