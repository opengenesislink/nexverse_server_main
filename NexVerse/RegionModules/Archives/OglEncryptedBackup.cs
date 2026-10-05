// OpenGenesisLINK encrypted backup container
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NexVerse.RegionModules.Archives
{
    public static class OglEncryptedBackup
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OGLBAK01");
        private const byte Version = 1;
        private const int SaltBytes = 16;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const int KeyBytes = 32;
        private const int DefaultIterations = 210000;

        public static void EncryptIar(string iarPath, string backupPath, string passphrase, int iterations = DefaultIterations)
        {
            if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
                throw new ArgumentException("Backup-Passphrase muss mindestens 12 Zeichen lang sein.", nameof(passphrase));
            if (iterations < 100000)
                throw new ArgumentOutOfRangeException(nameof(iterations), "PBKDF2-Iterationszahl ist zu niedrig.");

            byte[] plaintext = File.ReadAllBytes(iarPath);
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagBytes];
            byte[] header = BuildHeader(iterations, salt, nonce, plaintext.LongLength);

            try
            {
                using AesGcm aes = new(key, TagBytes);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, header);

                string temp = backupPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
                    using FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(header);
                    output.Write(ciphertext);
                    output.Write(tag);
                    output.Flush(true);
                    File.Move(temp, backupPath, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        public static void DecryptIar(string backupPath, string iarPath, string passphrase)
        {
            byte[] all = File.ReadAllBytes(backupPath);
            int headerLength = Magic.Length + 1 + 4 + SaltBytes + NonceBytes + 8;
            if (all.Length < headerLength + TagBytes)
                throw new InvalidDataException("OGL-Backup-Container ist unvollstaendig.");

            ReadOnlySpan<byte> header = all.AsSpan(0, headerLength);
            if (!header.Slice(0, Magic.Length).SequenceEqual(Magic) || header[Magic.Length] != Version)
                throw new InvalidDataException("Unbekanntes OGL-Backup-Format oder Version.");

            int offset = Magic.Length + 1;
            int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(offset, 4)); offset += 4;
            if (iterations < 100000 || iterations > 10000000)
                throw new InvalidDataException("Ungueltige KDF-Parameter im Backup.");
            byte[] salt = header.Slice(offset, SaltBytes).ToArray(); offset += SaltBytes;
            byte[] nonce = header.Slice(offset, NonceBytes).ToArray(); offset += NonceBytes;
            long plainLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(offset, 8));
            int cipherLength = all.Length - headerLength - TagBytes;
            if (plainLength < 0 || plainLength != cipherLength)
                throw new InvalidDataException("Backup-Laengenangabe ist ungueltig.");

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
            byte[] plaintext = new byte[cipherLength];
            try
            {
                using AesGcm aes = new(key, TagBytes);
                aes.Decrypt(nonce, all.AsSpan(headerLength, cipherLength), all.AsSpan(headerLength + cipherLength, TagBytes), plaintext, header);

                string temp = iarPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(iarPath))!);
                    File.WriteAllBytes(temp, plaintext);
                    File.Move(temp, iarPath, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("Backup-Authentifizierung fehlgeschlagen: Passphrase falsch oder Container manipuliert.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static byte[] BuildHeader(int iterations, byte[] salt, byte[] nonce, long plainLength)
        {
            byte[] header = new byte[Magic.Length + 1 + 4 + SaltBytes + NonceBytes + 8];
            int offset = 0;
            Magic.CopyTo(header, offset); offset += Magic.Length;
            header[offset++] = Version;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset, 4), iterations); offset += 4;
            salt.CopyTo(header, offset); offset += SaltBytes;
            nonce.CopyTo(header, offset); offset += NonceBytes;
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(offset, 8), plainLength);
            return header;
        }
    }
}
