// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NexVerse.Core.Economy
{
    public static class NexLedgerCurrency
    {
        public const string Code = "NVD";
        public const string Symbol = "NV$";
        public const int MinorUnits = 0;
    }

    public enum NexLedgerAccountClass
    {
        Resident = 1,
        Group = 2,
        Business = 3,
        Estate = 4,
        ObjectMerchantEndpoint = 5,
        System = 6,
        Escrow = 7
    }

    public enum NexLedgerSide
    {
        Debit = 1,
        Credit = 2
    }

    public sealed class NexLedgerAccount
    {
        public NexLedgerAccount(
            Guid accountId,
            NexLedgerAccountClass accountClass,
            NexLedgerSide normalSide,
            string reference,
            string displayName,
            string currencyCode = NexLedgerCurrency.Code,
            DateTimeOffset? createdAt = null)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException("Ledger account ID is required.", nameof(accountId));
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("Ledger account reference is required.", nameof(reference));
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("Ledger account display name is required.", nameof(displayName));
            if (normalSide != NexLedgerSide.Debit &&
                normalSide != NexLedgerSide.Credit)
            {
                throw new ArgumentOutOfRangeException(nameof(normalSide));
            }

            AccountId = accountId;
            AccountClass = accountClass;
            NormalSide = normalSide;
            Reference = reference.Trim();
            DisplayName = displayName.Trim();
            CurrencyCode = NormalizeCurrency(currencyCode);
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        }

        public Guid AccountId { get; }
        public NexLedgerAccountClass AccountClass { get; }
        public NexLedgerSide NormalSide { get; }
        public string Reference { get; }
        public string DisplayName { get; }
        public string CurrencyCode { get; }
        public DateTimeOffset CreatedAt { get; }

        private static string NormalizeCurrency(string value)
        {
            string currency =
                (value ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            if (!string.Equals(
                    currency,
                    NexLedgerCurrency.Code,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The initial NexVerse ledger supports only NVD / NV$.",
                    nameof(value));
            }

            return currency;
        }
    }

    public sealed class NexLedgerPosting
    {
        public NexLedgerPosting(
            Guid postingId,
            Guid accountId,
            NexLedgerSide side,
            long amountMinor,
            string memo = null)
        {
            if (postingId == Guid.Empty)
                throw new ArgumentException("Posting ID is required.", nameof(postingId));
            if (accountId == Guid.Empty)
                throw new ArgumentException("Posting account ID is required.", nameof(accountId));
            if (side != NexLedgerSide.Debit &&
                side != NexLedgerSide.Credit)
            {
                throw new ArgumentOutOfRangeException(nameof(side));
            }
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor), "Posting amount must be positive.");

            PostingId = postingId;
            AccountId = accountId;
            Side = side;
            AmountMinor = amountMinor;
            Memo = (memo ?? string.Empty).Trim();
        }

        public Guid PostingId { get; }
        public Guid AccountId { get; }
        public NexLedgerSide Side { get; }
        public long AmountMinor { get; }
        public string Memo { get; }
    }

    public sealed class NexLedgerTransaction
    {
        private readonly ReadOnlyCollection<NexLedgerPosting> m_Postings;
        private readonly ReadOnlyDictionary<string, string> m_Metadata;

        public NexLedgerTransaction(
            Guid transactionId,
            string kind,
            string reference,
            IEnumerable<NexLedgerPosting> postings,
            string correlationId = null,
            IReadOnlyDictionary<string, string> metadata = null,
            DateTimeOffset? occurredAt = null,
            string currencyCode = NexLedgerCurrency.Code)
        {
            if (transactionId == Guid.Empty)
                throw new ArgumentException("Ledger transaction ID is required.", nameof(transactionId));
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Ledger transaction kind is required.", nameof(kind));
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("Ledger transaction reference is required.", nameof(reference));

            TransactionId = transactionId;
            Kind = kind.Trim();
            Reference = reference.Trim();
            CorrelationId = (correlationId ?? string.Empty).Trim();
            CurrencyCode = NormalizeCurrency(currencyCode);
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow;

            NexLedgerPosting[] postingArray =
                (postings ?? throw new ArgumentNullException(nameof(postings)))
                    .ToArray();

            m_Postings =
                Array.AsReadOnly(postingArray);

            Dictionary<string, string> metadataCopy =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            if (metadata != null)
            {
                foreach (KeyValuePair<string, string> item in metadata)
                {
                    string key =
                        (item.Key ?? string.Empty)
                            .Trim();
                    if (key.Length == 0)
                        throw new ArgumentException("Ledger metadata keys cannot be empty.", nameof(metadata));

                    metadataCopy[key] =
                        item.Value ?? string.Empty;
                }
            }

            m_Metadata =
                new ReadOnlyDictionary<string, string>(
                    metadataCopy);

            Validate();
        }

        public Guid TransactionId { get; }
        public string Kind { get; }
        public string Reference { get; }
        public string CorrelationId { get; }
        public string CurrencyCode { get; }
        public DateTimeOffset OccurredAt { get; }
        public IReadOnlyList<NexLedgerPosting> Postings => m_Postings;
        public IReadOnlyDictionary<string, string> Metadata => m_Metadata;

        public void Validate()
        {
            if (m_Postings.Count < 2)
                throw new NexLedgerValidationException("A double-entry transaction requires at least two postings.");

            if (m_Postings.Select(x => x.PostingId).Distinct().Count() != m_Postings.Count)
                throw new NexLedgerValidationException("Posting IDs must be unique within a transaction.");

            if (m_Postings.Select(x => x.AccountId).Distinct().Count() < 2)
                throw new NexLedgerValidationException("A ledger transaction must involve at least two accounts.");

            long debit = 0;
            long credit = 0;

            try
            {
                checked
                {
                    foreach (NexLedgerPosting posting in m_Postings)
                    {
                        if (posting.AmountMinor <= 0)
                            throw new NexLedgerValidationException("Posting amounts must be positive.");

                        if (posting.Side == NexLedgerSide.Debit)
                            debit += posting.AmountMinor;
                        else if (posting.Side == NexLedgerSide.Credit)
                            credit += posting.AmountMinor;
                        else
                            throw new NexLedgerValidationException("Unknown ledger side.");
                    }
                }
            }
            catch (OverflowException e)
            {
                throw new NexLedgerValidationException(
                    "Ledger totals exceed the supported 64-bit amount range.",
                    e);
            }

            if (debit != credit)
            {
                throw new NexLedgerValidationException(
                    $"Unbalanced transaction: debit={debit}, credit={credit}.");
            }
        }

        private static string NormalizeCurrency(string value)
        {
            string currency =
                (value ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            if (!string.Equals(
                    currency,
                    NexLedgerCurrency.Code,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The initial NexVerse ledger supports only NVD / NV$.",
                    nameof(value));
            }

            return currency;
        }
    }

    public class NexLedgerValidationException : Exception
    {
        public NexLedgerValidationException(string message)
            : base(message)
        {
        }

        public NexLedgerValidationException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class NexLedgerConflictException : Exception
    {
        public NexLedgerConflictException(string message)
            : base(message)
        {
        }
    }
}
