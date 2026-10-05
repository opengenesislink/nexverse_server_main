// SPDX-License-Identifier: MPL-2.0

using System;
using System.Security.Cryptography;
using System.Text;

namespace NexVerse.Core.Economy
{
    /// <summary>
    /// OpenGenesisLINK-only virtual account identifier.
    /// NVBAN identifiers are deliberately not IBANs and must never be
    /// represented as real-world bank account numbers.
    /// </summary>
    public sealed class NexVirtualBankAccount
    {
        public const string Scheme = "NVBAN";
        public const string Prefix = "NVBAN-";
        public const int PayloadLength = 20;

        private const string Alphabet =
            "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        public NexVirtualBankAccount(
            Guid accountId,
            string identifier,
            DateTimeOffset? createdAt = null)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException(
                    "Ledger account ID is required.",
                    nameof(accountId));

            AccountId = accountId;
            Identifier = NormalizeIdentifier(identifier);
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        }

        public Guid AccountId { get; }
        public string Identifier { get; }
        public DateTimeOffset CreatedAt { get; }

        public static string CreateIdentifier(Guid accountId)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException(
                    "Ledger account ID is required.",
                    nameof(accountId));

            byte[] hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        "OpenGenesisLINK:NVBAN:v1:" +
                        accountId.ToString("D")));

            char[] payload = new char[PayloadLength];

            for (int i = 0; i < payload.Length; i++)
                payload[i] = Alphabet[hash[i] & 31];

            StringBuilder result =
                new StringBuilder(
                    Prefix,
                    Prefix.Length + PayloadLength + 4);

            for (int i = 0; i < payload.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                    result.Append('-');

                result.Append(payload[i]);
            }

            return result.ToString();
        }

        public static string NormalizeIdentifier(string value)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            if (!IsValidIdentifier(normalized))
                throw new ArgumentException(
                    "Invalid OpenGenesisLINK NVBAN virtual account identifier.",
                    nameof(value));

            return normalized;
        }

        public static bool IsValidIdentifier(string value)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            string[] parts = normalized.Split('-');

            if (parts.Length != 6 ||
                !string.Equals(parts[0], Scheme, StringComparison.Ordinal))
                return false;

            for (int part = 1; part < parts.Length; part++)
            {
                if (parts[part].Length != 4)
                    return false;

                for (int i = 0; i < parts[part].Length; i++)
                    if (Alphabet.IndexOf(parts[part][i]) < 0)
                        return false;
            }

            return true;
        }
    }

    public interface INexVirtualBankAccountStore
    {
        NexVirtualBankAccount GetVirtualBankAccount(Guid accountId);
        NexVirtualBankAccount GetVirtualBankAccountByIdentifier(string identifier);
        NexVirtualBankAccount GetOrCreateVirtualBankAccount(
            Guid accountId,
            string identifier,
            DateTimeOffset createdAt);
    }
}
