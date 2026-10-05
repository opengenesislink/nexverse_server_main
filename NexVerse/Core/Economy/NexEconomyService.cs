// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NexVerse.Core.Economy
{
    /// <summary>
    /// Policy boundary for all production NV$ movements.
    /// API and Viewer adapters must call this service rather than appending
    /// arbitrary ledger transactions directly.
    /// </summary>
    public sealed class NexEconomyService
    {
        private readonly NexDoubleEntryLedger m_Ledger;
        private readonly INexLedgerAccountStateStore m_States;

        public NexEconomyService(
            NexDoubleEntryLedger ledger,
            INexLedgerAccountStateStore states)
        {
            m_Ledger =
                ledger ??
                throw new ArgumentNullException(nameof(ledger));
            m_States =
                states ??
                throw new ArgumentNullException(nameof(states));
        }

        public NexLedgerAccount EnsureResidentAccount(
            Guid principalId,
            string displayName)
        {
            if (principalId == Guid.Empty)
                throw new ArgumentException("Resident principal ID is required.", nameof(principalId));

            NexLedgerAccount existing =
                m_Ledger.GetAccount(principalId);

            if (existing != null)
            {
                if (existing.AccountClass != NexLedgerAccountClass.Resident ||
                    existing.NormalSide != NexLedgerSide.Credit ||
                    !string.Equals(
                        existing.CurrencyCode,
                        NexLedgerCurrency.Code,
                        StringComparison.Ordinal))
                {
                    throw new NexLedgerConflictException(
                        "Resident UUID is already used by an incompatible ledger account.");
                }

                return existing;
            }

            NexLedgerAccount created =
                new NexLedgerAccount(
                    principalId,
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "resident:" + principalId.ToString("D"),
                    string.IsNullOrWhiteSpace(displayName)
                        ? principalId.ToString("D")
                        : displayName);

            if (m_Ledger.TryCreateAccount(created))
                return created;

            existing =
                m_Ledger.GetAccount(principalId);

            if (existing == null)
                throw new NexLedgerConflictException("Resident ledger account could not be created.");

            return existing;
        }

        public NexLedgerAccount GetAccount(Guid accountId) =>
            m_Ledger.GetAccount(accountId);

        public NexLedgerTransaction GetTransaction(Guid transactionId) =>
            m_Ledger.GetTransaction(transactionId);

        public IReadOnlyList<NexLedgerPosting> ListPostings(
            Guid accountId,
            int offset = 0,
            int limit = 100) =>
            m_Ledger.ListPostings(
                accountId,
                offset,
                limit);

        public long GetBalance(Guid accountId) =>
            m_Ledger.GetBalance(accountId);

        public NexLedgerAccountState GetAccountState(
            Guid accountId) =>
            m_States.GetAccountState(accountId);

        public IReadOnlyList<NexLedgerAccountStateEvent> ListAccountStateEvents(
            Guid accountId,
            int offset = 0,
            int limit = 100) =>
            m_States.ListAccountStateEvents(
                accountId,
                offset,
                limit);

        public NexLedgerAccountState SetAccountStatus(
            Guid accountId,
            NexLedgerAccountStatus status,
            string actor,
            string reason)
        {
            NexLedgerAccount account =
                RequireAccount(accountId);

            RequireActorReason(
                actor,
                reason);

            NexLedgerAccountState current =
                m_States.GetAccountState(accountId);

            if (current.Status == NexLedgerAccountStatus.Closed &&
                status != NexLedgerAccountStatus.Closed)
            {
                throw new NexLedgerPolicyException(
                    "Closed ledger accounts cannot be reopened.");
            }

            if (status == NexLedgerAccountStatus.Closed &&
                m_Ledger.GetBalance(account.AccountId) != 0)
            {
                throw new NexLedgerPolicyException(
                    "A ledger account can be closed only with zero balance.");
            }

            return m_States.SetAccountStatus(
                accountId,
                status,
                actor,
                reason);
        }

        public NexLedgerAppendResult Transfer(
            Guid fromAccountId,
            Guid toAccountId,
            long amountMinor,
            string reference,
            string correlationId = null,
            Guid? transactionId = null)
        {
            if (fromAccountId == toAccountId)
                throw new NexLedgerPolicyException("Source and destination accounts must differ.");
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));

            NexLedgerAccount from =
                RequireWalletAccount(
                    fromAccountId,
                    "source");
            NexLedgerAccount to =
                RequireWalletAccount(
                    toAccountId,
                    "destination");

            RequireActive(from.AccountId, "source");
            RequireActive(to.AccountId, "destination");
            RequireAvailableBalance(
                from,
                amountMinor);

            string normalizedReference =
                RequireReference(reference);
            Guid effectiveTransactionId =
                transactionId ??
                Guid.NewGuid();

            NexLedgerTransaction existing =
                m_Ledger.GetTransaction(
                    effectiveTransactionId);

            if (existing != null)
            {
                if (!MatchesTransfer(
                        existing,
                        from.AccountId,
                        to.AccountId,
                        amountMinor,
                        normalizedReference,
                        correlationId))
                {
                    throw new NexLedgerConflictException(
                        "Transaction ID already exists with different transfer content.");
                }

                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Duplicate,
                    existing);
            }

            NexLedgerTransaction transaction =
                new NexLedgerTransaction(
                    effectiveTransactionId,
                    "transfer",
                    normalizedReference,
                    new[]
                    {
                        new NexLedgerPosting(
                            DeterministicGuid(
                                "transfer-debit:" +
                                effectiveTransactionId.ToString("D")),
                            from.AccountId,
                            NexLedgerSide.Debit,
                            amountMinor,
                            "NV$ transfer debit"),
                        new NexLedgerPosting(
                            DeterministicGuid(
                                "transfer-credit:" +
                                effectiveTransactionId.ToString("D")),
                            to.AccountId,
                            NexLedgerSide.Credit,
                            amountMinor,
                            "NV$ transfer credit")
                    },
                    correlationId);

            try
            {
                return m_Ledger.Post(transaction);
            }
            catch (NexLedgerConflictException)
            {
                existing =
                    m_Ledger.GetTransaction(
                        effectiveTransactionId);

                if (existing != null &&
                    MatchesTransfer(
                        existing,
                        from.AccountId,
                        to.AccountId,
                        amountMinor,
                        normalizedReference,
                        correlationId))
                {
                    return new NexLedgerAppendResult(
                        NexLedgerAppendStatus.Duplicate,
                        existing);
                }

                throw;
            }
        }

        public NexLedgerAppendResult AdministrativeAdjustment(
            Guid targetAccountId,
            Guid systemCounterpartyId,
            long deltaMinor,
            string actor,
            string reason,
            string correlationId = null,
            Guid? transactionId = null)
        {
            if (deltaMinor == 0)
                throw new ArgumentOutOfRangeException(nameof(deltaMinor));

            RequireActorReason(
                actor,
                reason);

            NexLedgerAccount target =
                RequireAccount(targetAccountId);
            NexLedgerAccount system =
                RequireAccount(systemCounterpartyId);

            if (system.AccountClass != NexLedgerAccountClass.System)
            {
                throw new NexLedgerPolicyException(
                    "Administrative adjustments require a System counterparty account.");
            }

            RequireNotClosed(
                target.AccountId,
                "target");
            RequireNotClosed(
                system.AccountId,
                "system counterparty");

            long amount =
                deltaMinor == long.MinValue
                    ? throw new ArgumentOutOfRangeException(nameof(deltaMinor))
                    : Math.Abs(deltaMinor);

            NexLedgerPosting targetPosting;
            NexLedgerPosting systemPosting;

            if (deltaMinor > 0)
            {
                targetPosting =
                    new NexLedgerPosting(
                        Guid.NewGuid(),
                        target.AccountId,
                        NexLedgerSide.Credit,
                        amount,
                        reason);
                systemPosting =
                    new NexLedgerPosting(
                        Guid.NewGuid(),
                        system.AccountId,
                        NexLedgerSide.Debit,
                        amount,
                        reason);
            }
            else
            {
                if (target.NormalSide == NexLedgerSide.Credit)
                    RequireAvailableBalance(target, amount);

                targetPosting =
                    new NexLedgerPosting(
                        Guid.NewGuid(),
                        target.AccountId,
                        NexLedgerSide.Debit,
                        amount,
                        reason);
                systemPosting =
                    new NexLedgerPosting(
                        Guid.NewGuid(),
                        system.AccountId,
                        NexLedgerSide.Credit,
                        amount,
                        reason);
            }

            Guid adjustmentId =
                transactionId ??
                Guid.NewGuid();

            Dictionary<string, string> metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["actor"] = actor.Trim(),
                    ["reason"] = reason.Trim()
                };

            return m_Ledger.Post(
                new NexLedgerTransaction(
                    adjustmentId,
                    "admin_adjustment",
                    "ADJ-" + adjustmentId.ToString("N"),
                    new[]
                    {
                        systemPosting,
                        targetPosting
                    },
                    correlationId,
                    metadata));
        }

        public NexLedgerAppendResult Reverse(
            Guid originalTransactionId,
            string actor,
            string reason)
        {
            RequireActorReason(
                actor,
                reason);

            NexLedgerTransaction original =
                m_Ledger.GetTransaction(
                    originalTransactionId);

            if (original == null)
                throw new NexLedgerPolicyException("Original ledger transaction was not found.");

            if (string.Equals(
                    original.Kind,
                    "reversal",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new NexLedgerPolicyException("A reversal transaction cannot itself be reversed.");
            }

            Guid reversalId =
                DeterministicGuid(
                    "reversal:" +
                    original.TransactionId.ToString("D"));

            NexLedgerTransaction existing =
                m_Ledger.GetTransaction(
                    reversalId);

            if (existing != null)
            {
                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Duplicate,
                    existing);
            }

            List<NexLedgerPosting> postings =
                new List<NexLedgerPosting>(
                    original.Postings.Count);

            Dictionary<Guid, long> outgoing =
                new Dictionary<Guid, long>();

            foreach (NexLedgerPosting posting in
                     original.Postings)
            {
                NexLedgerAccount account =
                    RequireAccount(
                        posting.AccountId);

                RequireNotClosed(
                    account.AccountId,
                    "reversal account");

                NexLedgerSide reversedSide =
                    posting.Side == NexLedgerSide.Debit
                        ? NexLedgerSide.Credit
                        : NexLedgerSide.Debit;

                if (reversedSide == NexLedgerSide.Debit &&
                    account.NormalSide == NexLedgerSide.Credit)
                {
                    checked
                    {
                        outgoing[account.AccountId] =
                            outgoing.TryGetValue(
                                account.AccountId,
                                out long current)
                                ? current + posting.AmountMinor
                                : posting.AmountMinor;
                    }
                }

                postings.Add(
                    new NexLedgerPosting(
                        DeterministicGuid(
                            "reversal-posting:" +
                            posting.PostingId.ToString("D")),
                        posting.AccountId,
                        reversedSide,
                        posting.AmountMinor,
                        BoundedMemo(
                            "Reversal: " + posting.Memo)));
            }

            foreach (KeyValuePair<Guid, long> item in
                     outgoing)
            {
                RequireAvailableBalance(
                    RequireAccount(item.Key),
                    item.Value);
            }

            Dictionary<string, string> metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["reversal_of"] =
                        original.TransactionId.ToString("D"),
                    ["actor"] =
                        actor.Trim(),
                    ["reason"] =
                        reason.Trim()
                };

            NexLedgerTransaction reversal =
                new NexLedgerTransaction(
                    reversalId,
                    "reversal",
                    "REV-" +
                    original.TransactionId.ToString("N"),
                    postings,
                    original.CorrelationId,
                    metadata,
                    original.OccurredAt.AddTicks(1),
                    original.CurrencyCode);

            return m_Ledger.Post(reversal);
        }

        private NexLedgerAccount RequireAccount(
            Guid accountId)
        {
            NexLedgerAccount account =
                m_Ledger.GetAccount(accountId);

            if (account == null)
            {
                throw new NexLedgerPolicyException(
                    "Ledger account was not found: " +
                    accountId.ToString("D"));
            }

            return account;
        }

        private NexLedgerAccount RequireWalletAccount(
            Guid accountId,
            string role)
        {
            NexLedgerAccount account =
                RequireAccount(accountId);

            if (account.NormalSide != NexLedgerSide.Credit ||
                !IsWalletClass(account.AccountClass))
            {
                throw new NexLedgerPolicyException(
                    "The " +
                    role +
                    " account is not a transferable NV$ wallet.");
            }

            return account;
        }

        private static bool IsWalletClass(
            NexLedgerAccountClass accountClass) =>
            accountClass == NexLedgerAccountClass.Resident ||
            accountClass == NexLedgerAccountClass.Group ||
            accountClass == NexLedgerAccountClass.Business ||
            accountClass == NexLedgerAccountClass.Estate ||
            accountClass == NexLedgerAccountClass.ObjectMerchantEndpoint;

        private void RequireActive(
            Guid accountId,
            string role)
        {
            NexLedgerAccountState state =
                m_States.GetAccountState(accountId);

            if (state.Status != NexLedgerAccountStatus.Active)
            {
                throw new NexLedgerPolicyException(
                    "The " +
                    role +
                    " account is not active.");
            }
        }

        private void RequireNotClosed(
            Guid accountId,
            string role)
        {
            NexLedgerAccountState state =
                m_States.GetAccountState(accountId);

            if (state.Status == NexLedgerAccountStatus.Closed)
            {
                throw new NexLedgerPolicyException(
                    "The " +
                    role +
                    " account is closed.");
            }
        }

        private void RequireAvailableBalance(
            NexLedgerAccount account,
            long amountMinor)
        {
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));

            long balance =
                m_Ledger.GetBalance(
                    account.AccountId);

            if (balance < amountMinor)
            {
                throw new NexLedgerPolicyException(
                    "Insufficient NV$ balance.");
            }
        }

        private static string RequireReference(
            string value)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim();

            if (normalized.Length == 0)
                throw new ArgumentException("A transaction reference is required.", nameof(value));

            if (normalized.Length >
                NexLedgerTransaction.ReferenceLengthLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return normalized;
        }

        private static void RequireActorReason(
            string actor,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(actor))
                throw new ArgumentException("Administrative actor is required.", nameof(actor));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Administrative reason is required.", nameof(reason));

            if (actor.Trim().Length >
                NexLedgerAccountState.ActorLengthLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(actor));
            }

            if (reason.Trim().Length >
                NexLedgerAccountState.ReasonLengthLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        private static bool MatchesTransfer(
            NexLedgerTransaction transaction,
            Guid fromAccountId,
            Guid toAccountId,
            long amountMinor,
            string reference,
            string correlationId)
        {
            if (transaction == null ||
                !string.Equals(
                    transaction.Kind,
                    "transfer",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    transaction.Reference,
                    reference,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    transaction.CorrelationId,
                    (correlationId ?? string.Empty).Trim(),
                    StringComparison.Ordinal) ||
                transaction.Postings.Count != 2)
            {
                return false;
            }

            NexLedgerPosting debit =
                transaction.Postings[0];
            NexLedgerPosting credit =
                transaction.Postings[1];

            return
                debit.AccountId == fromAccountId &&
                debit.Side == NexLedgerSide.Debit &&
                debit.AmountMinor == amountMinor &&
                credit.AccountId == toAccountId &&
                credit.Side == NexLedgerSide.Credit &&
                credit.AmountMinor == amountMinor;
        }

        private static string BoundedMemo(
            string value)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim();

            if (normalized.Length <=
                NexLedgerPosting.MemoLengthLimit)
            {
                return normalized;
            }

            return normalized.Substring(
                0,
                NexLedgerPosting.MemoLengthLimit);
        }

        private static Guid DeterministicGuid(
            string value)
        {
            byte[] hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value));

            byte[] guidBytes =
                new byte[16];

            Array.Copy(
                hash,
                guidBytes,
                guidBytes.Length);

            return new Guid(guidBytes);
        }
    }
}
