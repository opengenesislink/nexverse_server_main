// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace NexVerse.Core.Economy
{
    public enum NexLedgerAppendStatus
    {
        Created = 1,
        Duplicate = 2
    }

    public sealed class NexLedgerAppendResult
    {
        public NexLedgerAppendResult(
            NexLedgerAppendStatus status,
            NexLedgerTransaction transaction)
        {
            Status = status;
            Transaction =
                transaction ??
                throw new ArgumentNullException(nameof(transaction));
        }

        public NexLedgerAppendStatus Status { get; }
        public NexLedgerTransaction Transaction { get; }
        public bool Created =>
            Status == NexLedgerAppendStatus.Created;
    }

    public interface INexLedgerStore
    {
        bool TryCreateAccount(NexLedgerAccount account);
        NexLedgerAccount GetAccount(Guid accountId);
        NexLedgerAppendResult Append(NexLedgerTransaction transaction);
        NexLedgerTransaction GetTransaction(Guid transactionId);
        long GetBalance(Guid accountId);
        IReadOnlyList<NexLedgerPosting> ListPostings(
            Guid accountId,
            int offset,
            int limit);
    }

    /// <summary>
    /// Deterministic reference store for tests and domain validation.
    /// It is deliberately not wired as a production economy backend because
    /// process memory is not durable accounting storage.
    /// </summary>
    public sealed class InMemoryNexLedgerStore :
        INexLedgerStore,
        INexLedgerAccountStateStore
    {
        private readonly object m_Sync =
            new object();
        private readonly Dictionary<Guid, NexLedgerAccount> m_Accounts =
            new Dictionary<Guid, NexLedgerAccount>();
        private readonly Dictionary<Guid, NexLedgerTransaction> m_Transactions =
            new Dictionary<Guid, NexLedgerTransaction>();
        private readonly HashSet<Guid> m_PostingIds =
            new HashSet<Guid>();
        private readonly Dictionary<Guid, List<NexLedgerPosting>> m_PostingsByAccount =
            new Dictionary<Guid, List<NexLedgerPosting>>();
        private readonly Dictionary<Guid, long> m_Balances =
            new Dictionary<Guid, long>();
        private readonly Dictionary<Guid, NexLedgerAccountState> m_AccountStates =
            new Dictionary<Guid, NexLedgerAccountState>();
        private readonly Dictionary<Guid, List<NexLedgerAccountStateEvent>> m_AccountStateEvents =
            new Dictionary<Guid, List<NexLedgerAccountStateEvent>>();

        public bool TryCreateAccount(
            NexLedgerAccount account)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            lock (m_Sync)
            {
                if (m_Accounts.ContainsKey(account.AccountId))
                    return false;

                if (m_Accounts.Values.Any(existing =>
                        string.Equals(
                            existing.Reference,
                            account.Reference,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            existing.CurrencyCode,
                            account.CurrencyCode,
                            StringComparison.Ordinal)))
                {
                    throw new NexLedgerConflictException(
                        "Ledger account reference already exists for this currency.");
                }

                m_Accounts.Add(
                    account.AccountId,
                    account);
                m_PostingsByAccount.Add(
                    account.AccountId,
                    new List<NexLedgerPosting>());
                m_Balances.Add(
                    account.AccountId,
                    0);
                m_AccountStates.Add(
                    account.AccountId,
                    new NexLedgerAccountState(
                        account.AccountId,
                        NexLedgerAccountStatus.Active,
                        0,
                        account.CreatedAt,
                        "system",
                        "account_created"));
                m_AccountStateEvents.Add(
                    account.AccountId,
                    new List<NexLedgerAccountStateEvent>());
                return true;
            }
        }

        public NexLedgerAccount GetAccount(
            Guid accountId)
        {
            lock (m_Sync)
            {
                m_Accounts.TryGetValue(
                    accountId,
                    out NexLedgerAccount account);
                return account;
            }
        }

        public NexLedgerAppendResult Append(
            NexLedgerTransaction transaction)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            transaction.Validate();

            lock (m_Sync)
            {
                if (m_Transactions.TryGetValue(
                        transaction.TransactionId,
                        out NexLedgerTransaction existing))
                {
                    if (!Equivalent(
                            existing,
                            transaction))
                    {
                        throw new NexLedgerConflictException(
                            "Transaction ID already exists with different immutable content.");
                    }

                    return new NexLedgerAppendResult(
                        NexLedgerAppendStatus.Duplicate,
                        existing);
                }

                Dictionary<Guid, long> nextBalances =
                    new Dictionary<Guid, long>();

                foreach (NexLedgerPosting posting in transaction.Postings)
                {
                    if (!m_Accounts.TryGetValue(
                            posting.AccountId,
                            out NexLedgerAccount account))
                    {
                        throw new NexLedgerValidationException(
                            $"Unknown ledger account {posting.AccountId}.");
                    }

                    if (!string.Equals(
                            account.CurrencyCode,
                            transaction.CurrencyCode,
                            StringComparison.Ordinal))
                    {
                        throw new NexLedgerValidationException(
                            "Cross-currency posting is not allowed.");
                    }

                    if (m_PostingIds.Contains(posting.PostingId))
                    {
                        throw new NexLedgerConflictException(
                            $"Posting ID {posting.PostingId} already exists.");
                    }

                    long current =
                        nextBalances.TryGetValue(
                            posting.AccountId,
                            out long staged)
                            ? staged
                            : m_Balances[posting.AccountId];

                    long delta =
                        posting.Side ==
                        account.NormalSide
                            ? posting.AmountMinor
                            : -posting.AmountMinor;

                    try
                    {
                        nextBalances[posting.AccountId] =
                            checked(current + delta);
                    }
                    catch (OverflowException e)
                    {
                        throw new NexLedgerValidationException(
                            "Account balance exceeds the supported 64-bit amount range.",
                            e);
                    }
                }

                m_Transactions.Add(
                    transaction.TransactionId,
                    transaction);

                foreach (NexLedgerPosting posting in transaction.Postings)
                {
                    m_PostingIds.Add(
                        posting.PostingId);
                    m_PostingsByAccount[posting.AccountId]
                        .Add(posting);
                }

                foreach (KeyValuePair<Guid, long> item in nextBalances)
                    m_Balances[item.Key] = item.Value;

                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Created,
                    transaction);
            }
        }

        public NexLedgerTransaction GetTransaction(
            Guid transactionId)
        {
            lock (m_Sync)
            {
                m_Transactions.TryGetValue(
                    transactionId,
                    out NexLedgerTransaction transaction);
                return transaction;
            }
        }

        public long GetBalance(
            Guid accountId)
        {
            lock (m_Sync)
            {
                if (!m_Accounts.ContainsKey(accountId))
                {
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                return m_Balances[accountId];
            }
        }

        public IReadOnlyList<NexLedgerPosting> ListPostings(
            Guid accountId,
            int offset,
            int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit));

            lock (m_Sync)
            {
                if (!m_PostingsByAccount.TryGetValue(
                        accountId,
                        out List<NexLedgerPosting> postings))
                {
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                return postings
                    .Skip(offset)
                    .Take(limit)
                    .ToArray();
            }
        }

        public NexLedgerAccountState GetAccountState(
            Guid accountId)
        {
            lock (m_Sync)
            {
                if (!m_Accounts.ContainsKey(accountId))
                {
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                return m_AccountStates[accountId];
            }
        }

        public NexLedgerAccountState SetAccountStatus(
            Guid accountId,
            NexLedgerAccountStatus status,
            string actor,
            string reason)
        {
            if (!Enum.IsDefined(typeof(NexLedgerAccountStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));

            lock (m_Sync)
            {
                if (!m_Accounts.ContainsKey(accountId))
                {
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                NexLedgerAccountState current =
                    m_AccountStates[accountId];

                if (current.Status ==
                    NexLedgerAccountStatus.Closed &&
                    status != NexLedgerAccountStatus.Closed)
                {
                    throw new NexLedgerPolicyException(
                        "Closed ledger accounts cannot be reopened.");
                }

                if (current.Status == status)
                    return current;

                long nextVersion =
                    checked(current.Version + 1);
                DateTimeOffset now =
                    DateTimeOffset.UtcNow;

                NexLedgerAccountState next =
                    new NexLedgerAccountState(
                        accountId,
                        status,
                        nextVersion,
                        now,
                        actor,
                        reason);

                NexLedgerAccountStateEvent accountEvent =
                    new NexLedgerAccountStateEvent(
                        Guid.NewGuid(),
                        accountId,
                        current.Status,
                        status,
                        nextVersion,
                        now,
                        actor,
                        reason);

                m_AccountStates[accountId] =
                    next;
                m_AccountStateEvents[accountId]
                    .Add(accountEvent);

                return next;
            }
        }

        public IReadOnlyList<NexLedgerAccountStateEvent> ListAccountStateEvents(
            Guid accountId,
            int offset,
            int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit));

            lock (m_Sync)
            {
                if (!m_AccountStateEvents.TryGetValue(
                        accountId,
                        out List<NexLedgerAccountStateEvent> events))
                {
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                return events
                    .Skip(offset)
                    .Take(limit)
                    .ToArray();
            }
        }

        private static bool Equivalent(
            NexLedgerTransaction left,
            NexLedgerTransaction right)
        {
            if (left.TransactionId != right.TransactionId ||
                left.OccurredAt != right.OccurredAt ||
                !string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) ||
                !string.Equals(left.Reference, right.Reference, StringComparison.Ordinal) ||
                !string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) ||
                !string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.Ordinal) ||
                left.Postings.Count != right.Postings.Count ||
                left.Metadata.Count != right.Metadata.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Postings.Count; i++)
            {
                NexLedgerPosting a = left.Postings[i];
                NexLedgerPosting b = right.Postings[i];

                if (a.PostingId != b.PostingId ||
                    a.AccountId != b.AccountId ||
                    a.Side != b.Side ||
                    a.AmountMinor != b.AmountMinor ||
                    !string.Equals(a.Memo, b.Memo, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            foreach (KeyValuePair<string, string> item in left.Metadata)
            {
                if (!right.Metadata.TryGetValue(
                        item.Key,
                        out string value) ||
                    !string.Equals(
                        item.Value,
                        value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class NexDoubleEntryLedger
    {
        private readonly INexLedgerStore m_Store;

        public NexDoubleEntryLedger(
            INexLedgerStore store)
        {
            m_Store =
                store ??
                throw new ArgumentNullException(nameof(store));
        }

        public bool TryCreateAccount(
            NexLedgerAccount account) =>
            m_Store.TryCreateAccount(account);

        public NexLedgerAppendResult Post(
            NexLedgerTransaction transaction) =>
            m_Store.Append(transaction);

        public NexLedgerAccount GetAccount(
            Guid accountId) =>
            m_Store.GetAccount(accountId);

        public NexLedgerTransaction GetTransaction(
            Guid transactionId) =>
            m_Store.GetTransaction(transactionId);

        public long GetBalance(
            Guid accountId) =>
            m_Store.GetBalance(accountId);

        public IReadOnlyList<NexLedgerPosting> ListPostings(
            Guid accountId,
            int offset = 0,
            int limit = 100) =>
            m_Store.ListPostings(
                accountId,
                offset,
                limit);
    }
}
