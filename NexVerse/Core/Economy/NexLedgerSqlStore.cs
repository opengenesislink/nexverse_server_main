// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NexVerse.Core.Economy
{
    public enum NexLedgerSqlDialect
    {
        MySql = 1,
        PostgreSql = 2,
        Sqlite = 3
    }

    /// <summary>
    /// Durable append-only SQL implementation of INexLedgerStore.
    ///
    /// The store intentionally persists no mutable balance column. Account
    /// balances are derived from immutable postings so a committed transaction
    /// is the single accounting source of truth.
    /// </summary>
    public sealed class NexLedgerSqlStore :
        INexLedgerStore,
        INexLedgerAccountStateStore,
        INexVirtualBankAccountStore,
        INexEconomyWorkflowStore
    {
        public const int CurrentSchemaVersion = 4;

        private const string SchemaTable = "ogl_ledger_schema";
        private const string AccountsTable = "ogl_ledger_accounts";
        private const string TransactionsTable = "ogl_ledger_transactions";
        private const string PostingsTable = "ogl_ledger_postings";
        private const string AccountStateTable = "ogl_ledger_account_state";
        private const string AccountEventsTable = "ogl_ledger_account_events";
        private const string VirtualAccountsTable = "ogl_ledger_virtual_accounts";
        private const string TransferPoliciesTable = "ogl_economy_transfer_policies";
        private const string PaymentRequestsTable = "ogl_economy_payment_requests";
        private const string CommerceOrdersTable = "ogl_economy_commerce_orders";
        private const string LandListingsTable = "ogl_economy_land_listings";
        private const string LandLeasesTable = "ogl_economy_land_leases";

        private readonly Func<DbConnection> m_ConnectionFactory;
        private readonly NexLedgerSqlDialect m_Dialect;

        public NexLedgerSqlStore(
            Func<DbConnection> connectionFactory,
            NexLedgerSqlDialect dialect,
            bool initializeSchema = true)
        {
            m_ConnectionFactory =
                connectionFactory ??
                throw new ArgumentNullException(nameof(connectionFactory));

            if (!Enum.IsDefined(typeof(NexLedgerSqlDialect), dialect))
                throw new ArgumentOutOfRangeException(nameof(dialect));

            m_Dialect = dialect;

            if (initializeSchema)
                EnsureSchema();
        }

        public void EnsureSchema()
        {
            using DbConnection connection =
                OpenConnection();

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {SchemaTable} (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at_utc_ticks BIGINT NOT NULL
                )");

            int version =
                GetSchemaVersion(connection);

            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Ledger schema version {version} is newer than supported version {CurrentSchemaVersion}.");
            }

            if (version < 1)
            {
                ApplySchemaVersion1(connection);
                version =
                    GetSchemaVersion(connection);
            }

            if (version < 2)
            {
                ApplySchemaVersion2(connection);
                version = GetSchemaVersion(connection);
            }

            if (version < 3)
            {
                ApplySchemaVersion3(connection);
                version = GetSchemaVersion(connection);
            }

            if (version < 4)
                ApplySchemaVersion4(connection);
        }

        public bool TryCreateAccount(
            NexLedgerAccount account)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            using DbConnection connection =
                OpenConnection();
            using DbTransaction transaction =
                connection.BeginTransaction(
                    IsolationLevel.Serializable);

            NexLedgerAccount existingById =
                GetAccount(
                    connection,
                    transaction,
                    account.AccountId);

            if (existingById != null)
            {
                transaction.Rollback();
                return false;
            }

            NexLedgerAccount existingByReference =
                GetAccountByReference(
                    connection,
                    transaction,
                    account.Reference,
                    account.CurrencyCode);

            if (existingByReference != null)
            {
                transaction.Rollback();
                throw new NexLedgerConflictException(
                    "Ledger account reference already exists for this currency.");
            }

            try
            {
                using DbCommand command =
                    CreateCommand(
                        connection,
                        transaction,
                        $@"INSERT INTO {AccountsTable}
                            (account_id, account_class, normal_side, account_reference,
                             display_name, currency_code, created_at_utc_ticks)
                           VALUES
                            (@account_id, @account_class, @normal_side, @account_reference,
                             @display_name, @currency_code, @created_at_utc_ticks)");

                AddParameter(command, "@account_id", account.AccountId.ToString("D"));
                AddParameter(command, "@account_class", (int)account.AccountClass);
                AddParameter(command, "@normal_side", (int)account.NormalSide);
                AddParameter(command, "@account_reference", account.Reference);
                AddParameter(command, "@display_name", account.DisplayName);
                AddParameter(command, "@currency_code", account.CurrencyCode);
                AddParameter(
                    command,
                    "@created_at_utc_ticks",
                    account.CreatedAt.UtcDateTime.Ticks);

                command.ExecuteNonQuery();

                InsertInitialAccountState(
                    connection,
                    transaction,
                    account);

                transaction.Commit();
                return true;
            }
            catch (DbException)
            {
                SafeRollback(transaction);

                using DbConnection verification =
                    OpenConnection();

                if (GetAccount(
                        verification,
                        null,
                        account.AccountId) != null)
                {
                    return false;
                }

                if (GetAccountByReference(
                        verification,
                        null,
                        account.Reference,
                        account.CurrencyCode) != null)
                {
                    throw new NexLedgerConflictException(
                        "Ledger account reference already exists for this currency.");
                }

                throw;
            }
        }

        public NexLedgerAccount GetAccount(
            Guid accountId)
        {
            if (accountId == Guid.Empty)
                return null;

            using DbConnection connection =
                OpenConnection();

            return GetAccount(
                connection,
                null,
                accountId);
        }

        public NexVirtualBankAccount GetVirtualBankAccount(Guid accountId)
        {
            if (accountId == Guid.Empty)
                return null;

            using DbConnection connection = OpenConnection();
            return GetVirtualBankAccount(connection, null, accountId);
        }

        public NexVirtualBankAccount GetVirtualBankAccountByIdentifier(string identifier)
        {
            string normalized =
                NexVirtualBankAccount.NormalizeIdentifier(identifier);

            using DbConnection connection = OpenConnection();
            return GetVirtualBankAccountByIdentifier(
                connection,
                null,
                normalized);
        }

        public NexVirtualBankAccount GetOrCreateVirtualBankAccount(
            Guid accountId,
            string identifier,
            DateTimeOffset createdAt)
        {
            string normalized =
                NexVirtualBankAccount.NormalizeIdentifier(identifier);

            using DbConnection connection = OpenConnection();
            using DbTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable);

            if (GetAccount(connection, transaction, accountId) == null)
            {
                transaction.Rollback();
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            NexVirtualBankAccount existing =
                GetVirtualBankAccount(connection, transaction, accountId);

            if (existing != null)
            {
                transaction.Rollback();

                if (!string.Equals(
                        existing.Identifier,
                        normalized,
                        StringComparison.Ordinal))
                {
                    throw new NexLedgerConflictException(
                        "Ledger account already has a different NVBAN identifier.");
                }

                return existing;
            }

            NexVirtualBankAccount collision =
                GetVirtualBankAccountByIdentifier(
                    connection,
                    transaction,
                    normalized);

            if (collision != null)
            {
                transaction.Rollback();

                if (collision.AccountId != accountId)
                    throw new NexLedgerConflictException(
                        "NVBAN identifier is already assigned to another ledger account.");

                return collision;
            }

            NexVirtualBankAccount created =
                new NexVirtualBankAccount(
                    accountId,
                    normalized,
                    createdAt);

            try
            {
                using DbCommand command =
                    CreateCommand(
                        connection,
                        transaction,
                        $@"INSERT INTO {VirtualAccountsTable}
                            (account_id, virtual_identifier, scheme, created_at_utc_ticks)
                           VALUES
                            (@account_id, @virtual_identifier, @scheme, @created_at_utc_ticks)");

                AddParameter(command, "@account_id", accountId.ToString("D"));
                AddParameter(command, "@virtual_identifier", created.Identifier);
                AddParameter(command, "@scheme", NexVirtualBankAccount.Scheme);
                AddParameter(
                    command,
                    "@created_at_utc_ticks",
                    created.CreatedAt.UtcDateTime.Ticks);

                command.ExecuteNonQuery();
                transaction.Commit();
                return created;
            }
            catch (DbException)
            {
                SafeRollback(transaction);

                using DbConnection verification = OpenConnection();

                NexVirtualBankAccount verified =
                    GetVirtualBankAccount(
                        verification,
                        null,
                        accountId);

                if (verified != null)
                {
                    if (!string.Equals(
                            verified.Identifier,
                            normalized,
                            StringComparison.Ordinal))
                    {
                        throw new NexLedgerConflictException(
                            "Ledger account already has a different NVBAN identifier.");
                    }

                    return verified;
                }

                NexVirtualBankAccount assigned =
                    GetVirtualBankAccountByIdentifier(
                        verification,
                        null,
                        normalized);

                if (assigned != null &&
                    assigned.AccountId != accountId)
                {
                    throw new NexLedgerConflictException(
                        "NVBAN identifier is already assigned to another ledger account.");
                }

                throw;
            }
        }

        public NexLedgerAccountState GetAccountState(
            Guid accountId)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException("Ledger account ID is required.", nameof(accountId));

            using DbConnection connection =
                OpenConnection();

            NexLedgerAccount account =
                GetAccount(
                    connection,
                    null,
                    accountId);

            if (account == null)
            {
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            return
                GetAccountState(
                    connection,
                    null,
                    accountId) ??
                new NexLedgerAccountState(
                    accountId,
                    NexLedgerAccountStatus.Active,
                    0,
                    account.CreatedAt,
                    "system",
                    "legacy_active");
        }

        public NexLedgerAccountState SetAccountStatus(
            Guid accountId,
            NexLedgerAccountStatus status,
            string actor,
            string reason)
        {
            if (!Enum.IsDefined(typeof(NexLedgerAccountStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));

            using DbConnection connection =
                OpenConnection();
            using DbTransaction transaction =
                connection.BeginTransaction(
                    IsolationLevel.Serializable);

            NexLedgerAccount account =
                GetAccount(
                    connection,
                    transaction,
                    accountId);

            if (account == null)
            {
                transaction.Rollback();
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            NexLedgerAccountState persisted =
                GetAccountState(
                    connection,
                    transaction,
                    accountId);

            NexLedgerAccountState current =
                persisted ??
                new NexLedgerAccountState(
                    accountId,
                    NexLedgerAccountStatus.Active,
                    0,
                    account.CreatedAt,
                    "system",
                    "legacy_active");

            if (current.Status ==
                NexLedgerAccountStatus.Closed &&
                status != NexLedgerAccountStatus.Closed)
            {
                transaction.Rollback();
                throw new NexLedgerPolicyException(
                    "Closed ledger accounts cannot be reopened.");
            }

            if (current.Status == status)
            {
                transaction.Rollback();
                return current;
            }

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

            try
            {
                if (persisted == null)
                {
                    InsertAccountState(
                        connection,
                        transaction,
                        next);
                }
                else
                {
                    using DbCommand update =
                        CreateCommand(
                            connection,
                            transaction,
                            $@"UPDATE {AccountStateTable}
                               SET status = @status,
                                   state_version = @next_version,
                                   changed_at_utc_ticks = @changed_at,
                                   changed_by = @changed_by,
                                   reason = @reason
                               WHERE account_id = @account_id
                                 AND state_version = @expected_version");

                    AddParameter(update, "@status", (int)next.Status);
                    AddParameter(update, "@next_version", next.Version);
                    AddParameter(update, "@changed_at", next.ChangedAt.UtcDateTime.Ticks);
                    AddParameter(update, "@changed_by", next.ChangedBy);
                    AddParameter(update, "@reason", next.Reason);
                    AddParameter(update, "@account_id", accountId.ToString("D"));
                    AddParameter(update, "@expected_version", current.Version);

                    if (update.ExecuteNonQuery() != 1)
                    {
                        throw new NexLedgerConflictException(
                            "Ledger account state changed concurrently.");
                    }
                }

                InsertAccountStateEvent(
                    connection,
                    transaction,
                    accountEvent);

                transaction.Commit();
                return next;
            }
            catch
            {
                SafeRollback(transaction);
                throw;
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

            using DbConnection connection =
                OpenConnection();

            if (GetAccount(
                    connection,
                    null,
                    accountId) == null)
            {
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT
                            event_id,
                            account_id,
                            previous_status,
                            new_status,
                            state_version,
                            occurred_at_utc_ticks,
                            actor,
                            reason
                       FROM {AccountEventsTable}
                       WHERE account_id = @account_id
                       ORDER BY state_version ASC
                       LIMIT @limit OFFSET @offset");

            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<NexLedgerAccountStateEvent> result =
                new List<NexLedgerAccountStateEvent>();

            using DbDataReader reader =
                command.ExecuteReader();

            while (reader.Read())
                result.Add(ReadAccountStateEvent(reader));

            return result;
        }

        public NexLedgerAppendResult Append(
            NexLedgerTransaction transaction)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            transaction.Validate();

            using DbConnection connection =
                OpenConnection();
            using DbTransaction dbTransaction =
                connection.BeginTransaction(
                    IsolationLevel.Serializable);

            NexLedgerTransaction existing =
                GetTransaction(
                    connection,
                    dbTransaction,
                    transaction.TransactionId);

            if (existing != null)
            {
                dbTransaction.Rollback();

                if (!Equivalent(existing, transaction))
                {
                    throw new NexLedgerConflictException(
                        "Transaction ID already exists with different immutable content.");
                }

                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Duplicate,
                    existing);
            }

            Dictionary<Guid, NexLedgerAccount> accounts =
                new Dictionary<Guid, NexLedgerAccount>();

            foreach (Guid accountId in
                     transaction.Postings
                         .Select(x => x.AccountId)
                         .Distinct())
            {
                NexLedgerAccount account =
                    GetAccount(
                        connection,
                        dbTransaction,
                        accountId);

                if (account == null)
                {
                    dbTransaction.Rollback();
                    throw new NexLedgerValidationException(
                        $"Unknown ledger account {accountId}.");
                }

                if (!string.Equals(
                        account.CurrencyCode,
                        transaction.CurrencyCode,
                        StringComparison.Ordinal))
                {
                    dbTransaction.Rollback();
                    throw new NexLedgerValidationException(
                        "Cross-currency posting is not allowed.");
                }

                accounts[accountId] =
                    account;
            }

            foreach (NexLedgerPosting posting in
                     transaction.Postings)
            {
                if (PostingExists(
                        connection,
                        dbTransaction,
                        posting.PostingId))
                {
                    dbTransaction.Rollback();
                    throw new NexLedgerConflictException(
                        $"Posting ID {posting.PostingId} already exists.");
                }
            }

            try
            {
                InsertTransaction(
                    connection,
                    dbTransaction,
                    transaction);

                for (int i = 0;
                     i < transaction.Postings.Count;
                     i++)
                {
                    InsertPosting(
                        connection,
                        dbTransaction,
                        transaction.TransactionId,
                        i,
                        transaction.Postings[i]);
                }

                dbTransaction.Commit();

                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Created,
                    transaction);
            }
            catch (DbException)
            {
                SafeRollback(dbTransaction);

                NexLedgerTransaction committed =
                    GetTransaction(
                        transaction.TransactionId);

                if (committed != null)
                {
                    if (!Equivalent(
                            committed,
                            transaction))
                    {
                        throw new NexLedgerConflictException(
                            "Transaction ID already exists with different immutable content.");
                    }

                    return new NexLedgerAppendResult(
                        NexLedgerAppendStatus.Duplicate,
                        committed);
                }

                using DbConnection verification =
                    OpenConnection();

                foreach (NexLedgerPosting posting in
                         transaction.Postings)
                {
                    if (PostingExists(
                            verification,
                            null,
                            posting.PostingId))
                    {
                        throw new NexLedgerConflictException(
                            $"Posting ID {posting.PostingId} already exists.");
                    }
                }

                throw;
            }
        }

        public NexLedgerTransaction GetTransaction(
            Guid transactionId)
        {
            if (transactionId == Guid.Empty)
                return null;

            using DbConnection connection =
                OpenConnection();

            return GetTransaction(
                connection,
                null,
                transactionId);
        }

        public long GetBalance(
            Guid accountId)
        {
            using DbConnection connection =
                OpenConnection();

            NexLedgerAccount account =
                GetAccount(
                    connection,
                    null,
                    accountId);

            if (account == null)
            {
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT COALESCE(
                            SUM(
                                CASE
                                    WHEN side = @normal_side THEN amount_minor
                                    ELSE -amount_minor
                                END
                            ),
                            0
                        )
                       FROM {PostingsTable}
                       WHERE account_id = @account_id");

            AddParameter(
                command,
                "@normal_side",
                (int)account.NormalSide);
            AddParameter(
                command,
                "@account_id",
                accountId.ToString("D"));

            object raw =
                command.ExecuteScalar();

            return raw == null ||
                   raw == DBNull.Value
                ? 0
                : Convert.ToInt64(
                    raw,
                    CultureInfo.InvariantCulture);
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

            using DbConnection connection =
                OpenConnection();

            if (GetAccount(
                    connection,
                    null,
                    accountId) == null)
            {
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");
            }

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT
                            p.posting_id,
                            p.account_id,
                            p.side,
                            p.amount_minor,
                            p.memo
                       FROM {PostingsTable} p
                       INNER JOIN {TransactionsTable} t
                           ON t.transaction_id = p.transaction_id
                       WHERE p.account_id = @account_id
                       ORDER BY
                            t.occurred_at_utc_ticks ASC,
                            p.sequence_no ASC
                       LIMIT @limit OFFSET @offset");

            AddParameter(
                command,
                "@account_id",
                accountId.ToString("D"));
            AddParameter(
                command,
                "@limit",
                limit);
            AddParameter(
                command,
                "@offset",
                offset);

            List<NexLedgerPosting> result =
                new List<NexLedgerPosting>();

            using DbDataReader reader =
                command.ExecuteReader();

            while (reader.Read())
            {
                result.Add(
                    ReadPosting(reader));
            }

            return result;
        }


        public long GetBalanceAt(
            Guid accountId,
            DateTimeOffset atOrBefore)
        {
            using DbConnection connection =
                OpenConnection();

            NexLedgerAccount account =
                GetAccount(
                    connection,
                    null,
                    accountId);

            if (account == null)
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT COALESCE(
                            SUM(
                                CASE
                                    WHEN p.side = @normal_side THEN p.amount_minor
                                    ELSE -p.amount_minor
                                END
                            ),
                            0
                        )
                       FROM {PostingsTable} p
                       INNER JOIN {TransactionsTable} t
                           ON t.transaction_id = p.transaction_id
                       WHERE p.account_id = @account_id
                         AND t.occurred_at_utc_ticks <= @at_ticks");

            AddParameter(command, "@normal_side", (int)account.NormalSide);
            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@at_ticks", atOrBefore.UtcDateTime.Ticks);

            object raw = command.ExecuteScalar();
            return raw == null || raw == DBNull.Value
                ? 0
                : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        public IReadOnlyList<NexLedgerTransaction> ListTransactions(
            Guid accountId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int offset,
            int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit));

            using DbConnection connection =
                OpenConnection();

            if (GetAccount(connection, null, accountId) == null)
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");

            StringBuilder sql =
                new StringBuilder(
                    $@"SELECT DISTINCT t.transaction_id, t.occurred_at_utc_ticks
                       FROM {TransactionsTable} t
                       INNER JOIN {PostingsTable} p
                           ON p.transaction_id = t.transaction_id
                       WHERE p.account_id = @account_id");

            if (from.HasValue)
                sql.Append(" AND t.occurred_at_utc_ticks >= @from_ticks");
            if (to.HasValue)
                sql.Append(" AND t.occurred_at_utc_ticks <= @to_ticks");

            sql.Append(
                " ORDER BY t.occurred_at_utc_ticks DESC, t.transaction_id DESC LIMIT @limit OFFSET @offset");

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    sql.ToString());

            AddParameter(command, "@account_id", accountId.ToString("D"));
            if (from.HasValue)
                AddParameter(command, "@from_ticks", from.Value.UtcDateTime.Ticks);
            if (to.HasValue)
                AddParameter(command, "@to_ticks", to.Value.UtcDateTime.Ticks);
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<Guid> ids = new List<Guid>();

            using (DbDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(
                        Guid.Parse(
                            Convert.ToString(
                                reader["transaction_id"],
                                CultureInfo.InvariantCulture)));
                }
            }

            List<NexLedgerTransaction> result =
                new List<NexLedgerTransaction>(ids.Count);

            foreach (Guid id in ids)
            {
                NexLedgerTransaction transaction =
                    GetTransaction(
                        connection,
                        null,
                        id);

                if (transaction != null)
                    result.Add(transaction);
            }

            return result;
        }

        public NexAccountTransferPolicy GetTransferPolicy(Guid accountId)
        {
            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT account_id, max_per_transfer_minor,
                              daily_outgoing_limit_minor, flat_fee_minor,
                              fee_account_id
                       FROM {TransferPoliciesTable}
                       WHERE account_id = @account_id");

            AddParameter(command, "@account_id", accountId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return ReadTransferPolicy(reader);
        }

        public NexAccountTransferPolicy SetTransferPolicy(
            NexAccountTransferPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            bool exists =
                GetTransferPolicy(
                    policy.AccountId) != null;

            using DbConnection connection = OpenConnection();

            if (GetAccount(connection, null, policy.AccountId) == null)
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {policy.AccountId}.");

            using DbTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable);

            if (exists)
            {
                using DbCommand update =
                    CreateCommand(
                        connection,
                        transaction,
                        $@"UPDATE {TransferPoliciesTable}
                           SET max_per_transfer_minor = @max_per_transfer,
                               daily_outgoing_limit_minor = @daily_limit,
                               flat_fee_minor = @flat_fee,
                               fee_account_id = @fee_account_id
                           WHERE account_id = @account_id");

                AddTransferPolicyParameters(update, policy);
                update.ExecuteNonQuery();
            }
            else
            {
                using DbCommand insert =
                    CreateCommand(
                        connection,
                        transaction,
                        $@"INSERT INTO {TransferPoliciesTable}
                            (account_id, max_per_transfer_minor,
                             daily_outgoing_limit_minor, flat_fee_minor,
                             fee_account_id)
                           VALUES
                            (@account_id, @max_per_transfer,
                             @daily_limit, @flat_fee,
                             @fee_account_id)");

                AddTransferPolicyParameters(insert, policy);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return policy;
        }

        public long GetOutgoingTotal(
            Guid accountId,
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive)
        {
            using DbConnection connection = OpenConnection();

            if (GetAccount(connection, null, accountId) == null)
                throw new NexLedgerValidationException(
                    $"Unknown ledger account {accountId}.");

            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT COALESCE(SUM(p.amount_minor), 0)
                       FROM {PostingsTable} p
                       INNER JOIN {TransactionsTable} t
                           ON t.transaction_id = p.transaction_id
                       WHERE p.account_id = @account_id
                         AND p.side = @debit_side
                         AND t.occurred_at_utc_ticks >= @from_ticks
                         AND t.occurred_at_utc_ticks < @to_ticks");

            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@debit_side", (int)NexLedgerSide.Debit);
            AddParameter(command, "@from_ticks", fromInclusive.UtcDateTime.Ticks);
            AddParameter(command, "@to_ticks", toExclusive.UtcDateTime.Ticks);

            object raw = command.ExecuteScalar();
            return raw == null || raw == DBNull.Value
                ? 0
                : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        public NexPaymentRequest CreatePaymentRequest(NexPaymentRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            NexPaymentRequest existing = GetPaymentRequest(request.RequestId);
            if (existing != null)
                return existing;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"INSERT INTO {PaymentRequestsTable}
                        (request_id, payee_account_id, payer_account_id,
                         amount_minor, request_reference, status,
                         created_at_utc_ticks, expires_at_utc_ticks,
                         payment_transaction_id)
                       VALUES
                        (@request_id, @payee_account_id, @payer_account_id,
                         @amount_minor, @request_reference, @status,
                         @created_at, @expires_at, @payment_transaction_id)");

            AddPaymentRequestParameters(command, request);
            command.ExecuteNonQuery();
            return request;
        }

        public NexPaymentRequest GetPaymentRequest(Guid requestId)
        {
            if (requestId == Guid.Empty)
                return null;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT request_id, payee_account_id, payer_account_id,
                              amount_minor, request_reference, status,
                              created_at_utc_ticks, expires_at_utc_ticks,
                              payment_transaction_id
                       FROM {PaymentRequestsTable}
                       WHERE request_id = @request_id");

            AddParameter(command, "@request_id", requestId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadPaymentRequest(reader) : null;
        }

        public IReadOnlyList<NexPaymentRequest> ListPaymentRequests(
            Guid accountId,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT request_id, payee_account_id, payer_account_id,
                              amount_minor, request_reference, status,
                              created_at_utc_ticks, expires_at_utc_ticks,
                              payment_transaction_id
                       FROM {PaymentRequestsTable}
                       WHERE payee_account_id = @account_id
                          OR payer_account_id = @account_id
                       ORDER BY created_at_utc_ticks DESC, request_id DESC
                       LIMIT @limit OFFSET @offset");

            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<NexPaymentRequest> result = new List<NexPaymentRequest>();
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadPaymentRequest(reader));
            return result;
        }

        public NexPaymentRequest UpdatePaymentRequest(NexPaymentRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (GetPaymentRequest(request.RequestId) == null)
                throw new NexLedgerValidationException("Payment request was not found.");

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"UPDATE {PaymentRequestsTable}
                       SET status = @status,
                           payment_transaction_id = @payment_transaction_id
                       WHERE request_id = @request_id");

            AddParameter(command, "@status", (int)request.Status);
            AddParameter(
                command,
                "@payment_transaction_id",
                request.PaymentTransactionId.ToString("D"));
            AddParameter(command, "@request_id", request.RequestId.ToString("D"));

            command.ExecuteNonQuery();
            return request;
        }

        public NexCommerceOrder CreateCommerceOrder(NexCommerceOrder order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));

            NexCommerceOrder existing = GetCommerceOrder(order.OrderId);
            if (existing != null)
                return existing;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"INSERT INTO {CommerceOrdersTable}
                        (order_id, kind, buyer_account_id, seller_account_id,
                         amount_minor, order_reference, external_reference,
                         status, payment_transaction_id, refund_transaction_id,
                         created_at_utc_ticks, completed_at_utc_ticks)
                       VALUES
                        (@order_id, @kind, @buyer_account_id, @seller_account_id,
                         @amount_minor, @order_reference, @external_reference,
                         @status, @payment_transaction_id, @refund_transaction_id,
                         @created_at, @completed_at)");

            AddCommerceOrderParameters(command, order);
            command.ExecuteNonQuery();
            return order;
        }

        public NexCommerceOrder GetCommerceOrder(Guid orderId)
        {
            if (orderId == Guid.Empty)
                return null;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT order_id, kind, buyer_account_id, seller_account_id,
                              amount_minor, order_reference, external_reference,
                              status, payment_transaction_id, refund_transaction_id,
                              created_at_utc_ticks, completed_at_utc_ticks
                       FROM {CommerceOrdersTable}
                       WHERE order_id = @order_id");

            AddParameter(command, "@order_id", orderId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadCommerceOrder(reader) : null;
        }

        public IReadOnlyList<NexCommerceOrder> ListCommerceOrders(
            Guid accountId,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT order_id, kind, buyer_account_id, seller_account_id,
                              amount_minor, order_reference, external_reference,
                              status, payment_transaction_id, refund_transaction_id,
                              created_at_utc_ticks, completed_at_utc_ticks
                       FROM {CommerceOrdersTable}
                       WHERE buyer_account_id = @account_id
                          OR seller_account_id = @account_id
                       ORDER BY created_at_utc_ticks DESC, order_id DESC
                       LIMIT @limit OFFSET @offset");

            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<NexCommerceOrder> result = new List<NexCommerceOrder>();
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadCommerceOrder(reader));
            return result;
        }

        public NexCommerceOrder UpdateCommerceOrder(NexCommerceOrder order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));

            if (GetCommerceOrder(order.OrderId) == null)
                throw new NexLedgerValidationException("Commerce order was not found.");

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"UPDATE {CommerceOrdersTable}
                       SET status = @status,
                           payment_transaction_id = @payment_transaction_id,
                           refund_transaction_id = @refund_transaction_id,
                           completed_at_utc_ticks = @completed_at
                       WHERE order_id = @order_id");

            AddParameter(command, "@status", (int)order.Status);
            AddParameter(command, "@payment_transaction_id", order.PaymentTransactionId.ToString("D"));
            AddParameter(command, "@refund_transaction_id", order.RefundTransactionId.ToString("D"));
            AddParameter(
                command,
                "@completed_at",
                order.CompletedAt.HasValue
                    ? order.CompletedAt.Value.UtcDateTime.Ticks
                    : 0L);
            AddParameter(command, "@order_id", order.OrderId.ToString("D"));

            command.ExecuteNonQuery();
            return order;
        }

        public NexLandListing CreateLandListing(NexLandListing listing)
        {
            if (listing == null)
                throw new ArgumentNullException(nameof(listing));

            NexLandListing existing = GetLandListing(listing.ListingId);
            if (existing != null)
                return existing;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"INSERT INTO {LandListingsTable}
                        (listing_id, listing_type, region_id, region_name,
                         parcel_id, parcel_local_id, parcel_name,
                         seller_account_id, estate_id, area, price_minor,
                         rental_period_days, active, created_at_utc_ticks)
                       VALUES
                        (@listing_id, @listing_type, @region_id, @region_name,
                         @parcel_id, @parcel_local_id, @parcel_name,
                         @seller_account_id, @estate_id, @area, @price_minor,
                         @rental_period_days, @active, @created_at)");

            AddLandListingParameters(command, listing);
            command.ExecuteNonQuery();
            return listing;
        }

        public NexLandListing GetLandListing(Guid listingId)
        {
            if (listingId == Guid.Empty)
                return null;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT listing_id, listing_type, region_id, region_name,
                              parcel_id, parcel_local_id, parcel_name,
                              seller_account_id, estate_id, area, price_minor,
                              rental_period_days, active, created_at_utc_ticks
                       FROM {LandListingsTable}
                       WHERE listing_id = @listing_id");

            AddParameter(command, "@listing_id", listingId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadLandListing(reader) : null;
        }

        public IReadOnlyList<NexLandListing> SearchLandListings(
            string query,
            NexLandListingType? type,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);
            string normalized = (query ?? string.Empty).Trim();

            using DbConnection connection = OpenConnection();
            StringBuilder sql =
                new StringBuilder(
                    $@"SELECT listing_id, listing_type, region_id, region_name,
                              parcel_id, parcel_local_id, parcel_name,
                              seller_account_id, estate_id, area, price_minor,
                              rental_period_days, active, created_at_utc_ticks
                       FROM {LandListingsTable}
                       WHERE active = 1");

            if (type.HasValue)
                sql.Append(" AND listing_type = @listing_type");

            if (normalized.Length > 0)
                sql.Append(" AND (LOWER(region_name) LIKE @query OR LOWER(parcel_name) LIKE @query)");

            sql.Append(" ORDER BY created_at_utc_ticks DESC, listing_id DESC LIMIT @limit OFFSET @offset");

            using DbCommand command =
                CreateCommand(connection, null, sql.ToString());

            if (type.HasValue)
                AddParameter(command, "@listing_type", (int)type.Value);
            if (normalized.Length > 0)
                AddParameter(command, "@query", "%" + normalized.ToLowerInvariant() + "%");
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<NexLandListing> result = new List<NexLandListing>();
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadLandListing(reader));
            return result;
        }

        public NexLandListing UpdateLandListing(NexLandListing listing)
        {
            if (listing == null)
                throw new ArgumentNullException(nameof(listing));

            if (GetLandListing(listing.ListingId) == null)
                throw new NexLedgerValidationException("Land listing was not found.");

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"UPDATE {LandListingsTable}
                       SET active = @active,
                           price_minor = @price_minor,
                           rental_period_days = @rental_period_days
                       WHERE listing_id = @listing_id");

            AddParameter(command, "@active", listing.Active ? 1 : 0);
            AddParameter(command, "@price_minor", listing.PriceMinor);
            AddParameter(command, "@rental_period_days", listing.RentalPeriodDays);
            AddParameter(command, "@listing_id", listing.ListingId.ToString("D"));

            command.ExecuteNonQuery();
            return listing;
        }

        public NexLandLease CreateLandLease(NexLandLease lease)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            NexLandLease existing = GetLandLease(lease.LeaseId);
            if (existing != null)
                return existing;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"INSERT INTO {LandLeasesTable}
                        (lease_id, listing_id, tenant_account_id, landlord_account_id,
                         rent_minor, period_days, starts_at_utc_ticks, ends_at_utc_ticks,
                         next_due_at_utc_ticks, active, last_payment_transaction_id)
                       VALUES
                        (@lease_id, @listing_id, @tenant_account_id, @landlord_account_id,
                         @rent_minor, @period_days, @starts_at, @ends_at,
                         @next_due_at, @active, @last_payment_transaction_id)");

            AddLandLeaseParameters(command, lease);
            command.ExecuteNonQuery();
            return lease;
        }

        public NexLandLease GetLandLease(Guid leaseId)
        {
            if (leaseId == Guid.Empty)
                return null;

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT lease_id, listing_id, tenant_account_id, landlord_account_id,
                              rent_minor, period_days, starts_at_utc_ticks, ends_at_utc_ticks,
                              next_due_at_utc_ticks, active, last_payment_transaction_id
                       FROM {LandLeasesTable}
                       WHERE lease_id = @lease_id");

            AddParameter(command, "@lease_id", leaseId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadLandLease(reader) : null;
        }

        public IReadOnlyList<NexLandLease> ListLandLeases(
            Guid accountId,
            int offset,
            int limit)
        {
            ValidatePage(offset, limit);

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT lease_id, listing_id, tenant_account_id, landlord_account_id,
                              rent_minor, period_days, starts_at_utc_ticks, ends_at_utc_ticks,
                              next_due_at_utc_ticks, active, last_payment_transaction_id
                       FROM {LandLeasesTable}
                       WHERE tenant_account_id = @account_id
                          OR landlord_account_id = @account_id
                       ORDER BY starts_at_utc_ticks DESC, lease_id DESC
                       LIMIT @limit OFFSET @offset");

            AddParameter(command, "@account_id", accountId.ToString("D"));
            AddParameter(command, "@limit", limit);
            AddParameter(command, "@offset", offset);

            List<NexLandLease> result = new List<NexLandLease>();
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadLandLease(reader));
            return result;
        }

        public NexLandLease UpdateLandLease(NexLandLease lease)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            if (GetLandLease(lease.LeaseId) == null)
                throw new NexLedgerValidationException("Land lease was not found.");

            using DbConnection connection = OpenConnection();
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"UPDATE {LandLeasesTable}
                       SET next_due_at_utc_ticks = @next_due_at,
                           active = @active,
                           last_payment_transaction_id = @last_payment_transaction_id
                       WHERE lease_id = @lease_id");

            AddParameter(command, "@next_due_at", lease.NextDueAt.UtcDateTime.Ticks);
            AddParameter(command, "@active", lease.Active ? 1 : 0);
            AddParameter(command, "@last_payment_transaction_id", lease.LastPaymentTransactionId.ToString("D"));
            AddParameter(command, "@lease_id", lease.LeaseId.ToString("D"));

            command.ExecuteNonQuery();
            return lease;
        }

        private NexVirtualBankAccount GetVirtualBankAccount(
            DbConnection connection,
            DbTransaction transaction,
            Guid accountId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT account_id, virtual_identifier, scheme, created_at_utc_ticks
                       FROM {VirtualAccountsTable}
                       WHERE account_id = @account_id");

            AddParameter(command, "@account_id", accountId.ToString("D"));

            using DbDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? ReadVirtualBankAccount(reader)
                : null;
        }

        private NexVirtualBankAccount GetVirtualBankAccountByIdentifier(
            DbConnection connection,
            DbTransaction transaction,
            string identifier)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT account_id, virtual_identifier, scheme, created_at_utc_ticks
                       FROM {VirtualAccountsTable}
                       WHERE virtual_identifier = @virtual_identifier");

            AddParameter(command, "@virtual_identifier", identifier);

            using DbDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? ReadVirtualBankAccount(reader)
                : null;
        }

        private static NexVirtualBankAccount ReadVirtualBankAccount(
            DbDataReader reader)
        {
            string scheme =
                Convert.ToString(
                    reader["scheme"],
                    CultureInfo.InvariantCulture);

            if (!string.Equals(
                    scheme,
                    NexVirtualBankAccount.Scheme,
                    StringComparison.Ordinal))
            {
                throw new NexLedgerValidationException(
                    "Unsupported virtual bank account scheme in ledger storage.");
            }

            return new NexVirtualBankAccount(
                Guid.Parse(
                    Convert.ToString(
                        reader["account_id"],
                        CultureInfo.InvariantCulture)),
                Convert.ToString(
                    reader["virtual_identifier"],
                    CultureInfo.InvariantCulture),
                new DateTimeOffset(
                    new DateTime(
                        Convert.ToInt64(
                            reader["created_at_utc_ticks"],
                            CultureInfo.InvariantCulture),
                        DateTimeKind.Utc)));
        }

        private void ApplySchemaVersion1(
            DbConnection connection)
        {
            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {AccountsTable} (
                    account_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    account_class INTEGER NOT NULL,
                    normal_side INTEGER NOT NULL,
                    account_reference VARCHAR(256) NOT NULL,
                    display_name VARCHAR(255) NOT NULL,
                    currency_code VARCHAR(8) NOT NULL,
                    created_at_utc_ticks BIGINT NOT NULL,
                    UNIQUE (account_reference, currency_code)
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {TransactionsTable} (
                    transaction_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    kind VARCHAR(64) NOT NULL,
                    transaction_reference VARCHAR(255) NOT NULL,
                    correlation_id VARCHAR(128) NOT NULL,
                    currency_code VARCHAR(8) NOT NULL,
                    occurred_at_utc_ticks BIGINT NOT NULL,
                    metadata_json TEXT NOT NULL
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {PostingsTable} (
                    posting_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    transaction_id VARCHAR(36) NOT NULL,
                    sequence_no INTEGER NOT NULL,
                    account_id VARCHAR(36) NOT NULL,
                    side INTEGER NOT NULL,
                    amount_minor BIGINT NOT NULL,
                    memo VARCHAR(255) NOT NULL,
                    UNIQUE (transaction_id, sequence_no),
                    UNIQUE (account_id, posting_id),
                    FOREIGN KEY (transaction_id)
                        REFERENCES {TransactionsTable}(transaction_id),
                    FOREIGN KEY (account_id)
                        REFERENCES {AccountsTable}(account_id)
                )");

            try
            {
                using DbCommand versionCommand =
                    CreateCommand(
                        connection,
                        null,
                        $@"INSERT INTO {SchemaTable}
                            (version, applied_at_utc_ticks)
                           VALUES
                            (@version, @applied_at_utc_ticks)");

                AddParameter(
                    versionCommand,
                    "@version",
                    1);
                AddParameter(
                    versionCommand,
                    "@applied_at_utc_ticks",
                    DateTimeOffset.UtcNow.UtcDateTime.Ticks);

                versionCommand.ExecuteNonQuery();
            }
            catch (DbException)
            {
                if (GetSchemaVersion(connection) < 1)
                    throw;
            }
        }

        private void ApplySchemaVersion2(
            DbConnection connection)
        {
            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {AccountStateTable} (
                    account_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    status INTEGER NOT NULL,
                    state_version BIGINT NOT NULL,
                    changed_at_utc_ticks BIGINT NOT NULL,
                    changed_by VARCHAR(128) NOT NULL,
                    reason VARCHAR(255) NOT NULL,
                    FOREIGN KEY (account_id)
                        REFERENCES {AccountsTable}(account_id)
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {AccountEventsTable} (
                    event_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    account_id VARCHAR(36) NOT NULL,
                    previous_status INTEGER NOT NULL,
                    new_status INTEGER NOT NULL,
                    state_version BIGINT NOT NULL,
                    occurred_at_utc_ticks BIGINT NOT NULL,
                    actor VARCHAR(128) NOT NULL,
                    reason VARCHAR(255) NOT NULL,
                    UNIQUE (account_id, state_version),
                    FOREIGN KEY (account_id)
                        REFERENCES {AccountsTable}(account_id)
                )");

            try
            {
                using DbCommand versionCommand =
                    CreateCommand(
                        connection,
                        null,
                        $@"INSERT INTO {SchemaTable}
                            (version, applied_at_utc_ticks)
                           VALUES
                            (@version, @applied_at_utc_ticks)");

                AddParameter(versionCommand, "@version", 2);
                AddParameter(
                    versionCommand,
                    "@applied_at_utc_ticks",
                    DateTimeOffset.UtcNow.UtcDateTime.Ticks);

                versionCommand.ExecuteNonQuery();
            }
            catch (DbException)
            {
                if (GetSchemaVersion(connection) < 2)
                    throw;
            }
        }

        private void ApplySchemaVersion3(
            DbConnection connection)
        {
            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {VirtualAccountsTable} (
                    account_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    virtual_identifier VARCHAR(64) NOT NULL,
                    scheme VARCHAR(16) NOT NULL,
                    created_at_utc_ticks BIGINT NOT NULL,
                    UNIQUE (virtual_identifier),
                    FOREIGN KEY (account_id)
                        REFERENCES {AccountsTable}(account_id)
                )");

            try
            {
                using DbCommand versionCommand =
                    CreateCommand(
                        connection,
                        null,
                        $@"INSERT INTO {SchemaTable}
                            (version, applied_at_utc_ticks)
                           VALUES
                            (@version, @applied_at_utc_ticks)");

                AddParameter(versionCommand, "@version", 3);
                AddParameter(
                    versionCommand,
                    "@applied_at_utc_ticks",
                    DateTimeOffset.UtcNow.UtcDateTime.Ticks);

                versionCommand.ExecuteNonQuery();
            }
            catch (DbException)
            {
                if (GetSchemaVersion(connection) < 3)
                    throw;
            }
        }


        private void ApplySchemaVersion4(
            DbConnection connection)
        {
            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {TransferPoliciesTable} (
                    account_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    max_per_transfer_minor BIGINT NOT NULL,
                    daily_outgoing_limit_minor BIGINT NOT NULL,
                    flat_fee_minor BIGINT NOT NULL,
                    fee_account_id VARCHAR(36) NOT NULL,
                    FOREIGN KEY (account_id)
                        REFERENCES {AccountsTable}(account_id)
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {PaymentRequestsTable} (
                    request_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    payee_account_id VARCHAR(36) NOT NULL,
                    payer_account_id VARCHAR(36) NOT NULL,
                    amount_minor BIGINT NOT NULL,
                    request_reference VARCHAR(255) NOT NULL,
                    status INTEGER NOT NULL,
                    created_at_utc_ticks BIGINT NOT NULL,
                    expires_at_utc_ticks BIGINT NOT NULL,
                    payment_transaction_id VARCHAR(36) NOT NULL
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {CommerceOrdersTable} (
                    order_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    buyer_account_id VARCHAR(36) NOT NULL,
                    seller_account_id VARCHAR(36) NOT NULL,
                    amount_minor BIGINT NOT NULL,
                    order_reference VARCHAR(255) NOT NULL,
                    external_reference VARCHAR(255) NOT NULL,
                    status INTEGER NOT NULL,
                    payment_transaction_id VARCHAR(36) NOT NULL,
                    refund_transaction_id VARCHAR(36) NOT NULL,
                    created_at_utc_ticks BIGINT NOT NULL,
                    completed_at_utc_ticks BIGINT NOT NULL
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {LandListingsTable} (
                    listing_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    listing_type INTEGER NOT NULL,
                    region_id VARCHAR(36) NOT NULL,
                    region_name VARCHAR(255) NOT NULL,
                    parcel_id VARCHAR(36) NOT NULL,
                    parcel_local_id INTEGER NOT NULL,
                    parcel_name VARCHAR(255) NOT NULL,
                    seller_account_id VARCHAR(36) NOT NULL,
                    estate_id VARCHAR(36) NOT NULL,
                    area INTEGER NOT NULL,
                    price_minor BIGINT NOT NULL,
                    rental_period_days INTEGER NOT NULL,
                    active INTEGER NOT NULL,
                    created_at_utc_ticks BIGINT NOT NULL
                )");

            ExecuteNonQuery(
                connection,
                null,
                $@"CREATE TABLE IF NOT EXISTS {LandLeasesTable} (
                    lease_id VARCHAR(36) NOT NULL PRIMARY KEY,
                    listing_id VARCHAR(36) NOT NULL,
                    tenant_account_id VARCHAR(36) NOT NULL,
                    landlord_account_id VARCHAR(36) NOT NULL,
                    rent_minor BIGINT NOT NULL,
                    period_days INTEGER NOT NULL,
                    starts_at_utc_ticks BIGINT NOT NULL,
                    ends_at_utc_ticks BIGINT NOT NULL,
                    next_due_at_utc_ticks BIGINT NOT NULL,
                    active INTEGER NOT NULL,
                    last_payment_transaction_id VARCHAR(36) NOT NULL
                )");

            try
            {
                using DbCommand versionCommand =
                    CreateCommand(
                        connection,
                        null,
                        $@"INSERT INTO {SchemaTable}
                            (version, applied_at_utc_ticks)
                           VALUES
                            (@version, @applied_at_utc_ticks)");

                AddParameter(versionCommand, "@version", 4);
                AddParameter(
                    versionCommand,
                    "@applied_at_utc_ticks",
                    DateTimeOffset.UtcNow.UtcDateTime.Ticks);

                versionCommand.ExecuteNonQuery();
            }
            catch (DbException)
            {
                if (GetSchemaVersion(connection) < 4)
                    throw;
            }
        }

        private int GetSchemaVersion(
            DbConnection connection)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    null,
                    $@"SELECT COALESCE(MAX(version), 0)
                       FROM {SchemaTable}");

            object raw =
                command.ExecuteScalar();

            return raw == null ||
                   raw == DBNull.Value
                ? 0
                : Convert.ToInt32(
                    raw,
                    CultureInfo.InvariantCulture);
        }

        private void InsertInitialAccountState(
            DbConnection connection,
            DbTransaction transaction,
            NexLedgerAccount account)
        {
            InsertAccountState(
                connection,
                transaction,
                new NexLedgerAccountState(
                    account.AccountId,
                    NexLedgerAccountStatus.Active,
                    0,
                    account.CreatedAt,
                    "system",
                    "account_created"));
        }

        private void InsertAccountState(
            DbConnection connection,
            DbTransaction transaction,
            NexLedgerAccountState state)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"INSERT INTO {AccountStateTable}
                        (account_id, status, state_version, changed_at_utc_ticks,
                         changed_by, reason)
                       VALUES
                        (@account_id, @status, @state_version, @changed_at,
                         @changed_by, @reason)");

            AddParameter(command, "@account_id", state.AccountId.ToString("D"));
            AddParameter(command, "@status", (int)state.Status);
            AddParameter(command, "@state_version", state.Version);
            AddParameter(command, "@changed_at", state.ChangedAt.UtcDateTime.Ticks);
            AddParameter(command, "@changed_by", state.ChangedBy);
            AddParameter(command, "@reason", state.Reason);
            command.ExecuteNonQuery();
        }

        private void InsertAccountStateEvent(
            DbConnection connection,
            DbTransaction transaction,
            NexLedgerAccountStateEvent accountEvent)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"INSERT INTO {AccountEventsTable}
                        (event_id, account_id, previous_status, new_status,
                         state_version, occurred_at_utc_ticks, actor, reason)
                       VALUES
                        (@event_id, @account_id, @previous_status, @new_status,
                         @state_version, @occurred_at, @actor, @reason)");

            AddParameter(command, "@event_id", accountEvent.EventId.ToString("D"));
            AddParameter(command, "@account_id", accountEvent.AccountId.ToString("D"));
            AddParameter(command, "@previous_status", (int)accountEvent.PreviousStatus);
            AddParameter(command, "@new_status", (int)accountEvent.NewStatus);
            AddParameter(command, "@state_version", accountEvent.Version);
            AddParameter(command, "@occurred_at", accountEvent.OccurredAt.UtcDateTime.Ticks);
            AddParameter(command, "@actor", accountEvent.Actor);
            AddParameter(command, "@reason", accountEvent.Reason);
            command.ExecuteNonQuery();
        }

        private NexLedgerAccountState GetAccountState(
            DbConnection connection,
            DbTransaction transaction,
            Guid accountId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT
                            account_id,
                            status,
                            state_version,
                            changed_at_utc_ticks,
                            changed_by,
                            reason
                       FROM {AccountStateTable}
                       WHERE account_id = @account_id");

            AddParameter(command, "@account_id", accountId.ToString("D"));

            using DbDataReader reader =
                command.ExecuteReader();

            return reader.Read()
                ? ReadAccountState(reader)
                : null;
        }

        private static NexLedgerAccountState ReadAccountState(
            DbDataReader reader)
        {
            return new NexLedgerAccountState(
                Guid.Parse(Convert.ToString(reader["account_id"], CultureInfo.InvariantCulture)),
                (NexLedgerAccountStatus)Convert.ToInt32(reader["status"], CultureInfo.InvariantCulture),
                Convert.ToInt64(reader["state_version"], CultureInfo.InvariantCulture),
                new DateTimeOffset(
                    new DateTime(
                        Convert.ToInt64(reader["changed_at_utc_ticks"], CultureInfo.InvariantCulture),
                        DateTimeKind.Utc)),
                Convert.ToString(reader["changed_by"], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader["reason"], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        private static NexLedgerAccountStateEvent ReadAccountStateEvent(
            DbDataReader reader)
        {
            return new NexLedgerAccountStateEvent(
                Guid.Parse(Convert.ToString(reader["event_id"], CultureInfo.InvariantCulture)),
                Guid.Parse(Convert.ToString(reader["account_id"], CultureInfo.InvariantCulture)),
                (NexLedgerAccountStatus)Convert.ToInt32(reader["previous_status"], CultureInfo.InvariantCulture),
                (NexLedgerAccountStatus)Convert.ToInt32(reader["new_status"], CultureInfo.InvariantCulture),
                Convert.ToInt64(reader["state_version"], CultureInfo.InvariantCulture),
                new DateTimeOffset(
                    new DateTime(
                        Convert.ToInt64(reader["occurred_at_utc_ticks"], CultureInfo.InvariantCulture),
                        DateTimeKind.Utc)),
                Convert.ToString(reader["actor"], CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(reader["reason"], CultureInfo.InvariantCulture) ?? string.Empty);
        }


        private static void ValidatePage(int offset, int limit)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (limit < 1 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit));
        }

        private static Guid ReadGuid(DbDataReader reader, string name) =>
            Guid.Parse(
                Convert.ToString(
                    reader[name],
                    CultureInfo.InvariantCulture));

        private static DateTimeOffset ReadTime(DbDataReader reader, string name) =>
            new DateTimeOffset(
                new DateTime(
                    Convert.ToInt64(
                        reader[name],
                        CultureInfo.InvariantCulture),
                    DateTimeKind.Utc));

        private static void AddTransferPolicyParameters(
            DbCommand command,
            NexAccountTransferPolicy policy)
        {
            AddParameter(command, "@account_id", policy.AccountId.ToString("D"));
            AddParameter(command, "@max_per_transfer", policy.MaxPerTransferMinor);
            AddParameter(command, "@daily_limit", policy.DailyOutgoingLimitMinor);
            AddParameter(command, "@flat_fee", policy.FlatFeeMinor);
            AddParameter(command, "@fee_account_id", policy.FeeAccountId.ToString("D"));
        }

        private static NexAccountTransferPolicy ReadTransferPolicy(DbDataReader reader) =>
            new NexAccountTransferPolicy(
                ReadGuid(reader, "account_id"),
                Convert.ToInt64(reader["max_per_transfer_minor"], CultureInfo.InvariantCulture),
                Convert.ToInt64(reader["daily_outgoing_limit_minor"], CultureInfo.InvariantCulture),
                Convert.ToInt64(reader["flat_fee_minor"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "fee_account_id"));

        private static void AddPaymentRequestParameters(
            DbCommand command,
            NexPaymentRequest request)
        {
            AddParameter(command, "@request_id", request.RequestId.ToString("D"));
            AddParameter(command, "@payee_account_id", request.PayeeAccountId.ToString("D"));
            AddParameter(command, "@payer_account_id", request.PayerAccountId.ToString("D"));
            AddParameter(command, "@amount_minor", request.AmountMinor);
            AddParameter(command, "@request_reference", request.Reference);
            AddParameter(command, "@status", (int)request.Status);
            AddParameter(command, "@created_at", request.CreatedAt.UtcDateTime.Ticks);
            AddParameter(command, "@expires_at", request.ExpiresAt.UtcDateTime.Ticks);
            AddParameter(command, "@payment_transaction_id", request.PaymentTransactionId.ToString("D"));
        }

        private static NexPaymentRequest ReadPaymentRequest(DbDataReader reader) =>
            new NexPaymentRequest(
                ReadGuid(reader, "request_id"),
                ReadGuid(reader, "payee_account_id"),
                ReadGuid(reader, "payer_account_id"),
                Convert.ToInt64(reader["amount_minor"], CultureInfo.InvariantCulture),
                Convert.ToString(reader["request_reference"], CultureInfo.InvariantCulture),
                (NexPaymentRequestStatus)Convert.ToInt32(reader["status"], CultureInfo.InvariantCulture),
                ReadTime(reader, "created_at_utc_ticks"),
                ReadTime(reader, "expires_at_utc_ticks"),
                ReadGuid(reader, "payment_transaction_id"));

        private static void AddCommerceOrderParameters(
            DbCommand command,
            NexCommerceOrder order)
        {
            AddParameter(command, "@order_id", order.OrderId.ToString("D"));
            AddParameter(command, "@kind", (int)order.Kind);
            AddParameter(command, "@buyer_account_id", order.BuyerAccountId.ToString("D"));
            AddParameter(command, "@seller_account_id", order.SellerAccountId.ToString("D"));
            AddParameter(command, "@amount_minor", order.AmountMinor);
            AddParameter(command, "@order_reference", order.Reference);
            AddParameter(command, "@external_reference", order.ExternalReference);
            AddParameter(command, "@status", (int)order.Status);
            AddParameter(command, "@payment_transaction_id", order.PaymentTransactionId.ToString("D"));
            AddParameter(command, "@refund_transaction_id", order.RefundTransactionId.ToString("D"));
            AddParameter(command, "@created_at", order.CreatedAt.UtcDateTime.Ticks);
            AddParameter(
                command,
                "@completed_at",
                order.CompletedAt.HasValue
                    ? order.CompletedAt.Value.UtcDateTime.Ticks
                    : 0L);
        }

        private static NexCommerceOrder ReadCommerceOrder(DbDataReader reader)
        {
            long completedTicks =
                Convert.ToInt64(
                    reader["completed_at_utc_ticks"],
                    CultureInfo.InvariantCulture);

            return new NexCommerceOrder(
                ReadGuid(reader, "order_id"),
                (NexCommerceKind)Convert.ToInt32(reader["kind"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "buyer_account_id"),
                ReadGuid(reader, "seller_account_id"),
                Convert.ToInt64(reader["amount_minor"], CultureInfo.InvariantCulture),
                Convert.ToString(reader["order_reference"], CultureInfo.InvariantCulture),
                Convert.ToString(reader["external_reference"], CultureInfo.InvariantCulture),
                (NexCommerceOrderStatus)Convert.ToInt32(reader["status"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "payment_transaction_id"),
                ReadGuid(reader, "refund_transaction_id"),
                ReadTime(reader, "created_at_utc_ticks"),
                completedTicks > 0
                    ? new DateTimeOffset(new DateTime(completedTicks, DateTimeKind.Utc))
                    : (DateTimeOffset?)null);
        }

        private static void AddLandListingParameters(
            DbCommand command,
            NexLandListing listing)
        {
            AddParameter(command, "@listing_id", listing.ListingId.ToString("D"));
            AddParameter(command, "@listing_type", (int)listing.ListingType);
            AddParameter(command, "@region_id", listing.RegionId.ToString("D"));
            AddParameter(command, "@region_name", listing.RegionName);
            AddParameter(command, "@parcel_id", listing.ParcelId.ToString("D"));
            AddParameter(command, "@parcel_local_id", listing.ParcelLocalId);
            AddParameter(command, "@parcel_name", listing.ParcelName);
            AddParameter(command, "@seller_account_id", listing.SellerAccountId.ToString("D"));
            AddParameter(command, "@estate_id", listing.EstateId.ToString("D"));
            AddParameter(command, "@area", listing.Area);
            AddParameter(command, "@price_minor", listing.PriceMinor);
            AddParameter(command, "@rental_period_days", listing.RentalPeriodDays);
            AddParameter(command, "@active", listing.Active ? 1 : 0);
            AddParameter(command, "@created_at", listing.CreatedAt.UtcDateTime.Ticks);
        }

        private static NexLandListing ReadLandListing(DbDataReader reader) =>
            new NexLandListing(
                ReadGuid(reader, "listing_id"),
                (NexLandListingType)Convert.ToInt32(reader["listing_type"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "region_id"),
                Convert.ToString(reader["region_name"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "parcel_id"),
                Convert.ToInt32(reader["parcel_local_id"], CultureInfo.InvariantCulture),
                Convert.ToString(reader["parcel_name"], CultureInfo.InvariantCulture),
                ReadGuid(reader, "seller_account_id"),
                ReadGuid(reader, "estate_id"),
                Convert.ToInt32(reader["area"], CultureInfo.InvariantCulture),
                Convert.ToInt64(reader["price_minor"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["rental_period_days"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["active"], CultureInfo.InvariantCulture) != 0,
                ReadTime(reader, "created_at_utc_ticks"));

        private static void AddLandLeaseParameters(
            DbCommand command,
            NexLandLease lease)
        {
            AddParameter(command, "@lease_id", lease.LeaseId.ToString("D"));
            AddParameter(command, "@listing_id", lease.ListingId.ToString("D"));
            AddParameter(command, "@tenant_account_id", lease.TenantAccountId.ToString("D"));
            AddParameter(command, "@landlord_account_id", lease.LandlordAccountId.ToString("D"));
            AddParameter(command, "@rent_minor", lease.RentMinor);
            AddParameter(command, "@period_days", lease.PeriodDays);
            AddParameter(command, "@starts_at", lease.StartsAt.UtcDateTime.Ticks);
            AddParameter(command, "@ends_at", lease.EndsAt.UtcDateTime.Ticks);
            AddParameter(command, "@next_due_at", lease.NextDueAt.UtcDateTime.Ticks);
            AddParameter(command, "@active", lease.Active ? 1 : 0);
            AddParameter(command, "@last_payment_transaction_id", lease.LastPaymentTransactionId.ToString("D"));
        }

        private static NexLandLease ReadLandLease(DbDataReader reader) =>
            new NexLandLease(
                ReadGuid(reader, "lease_id"),
                ReadGuid(reader, "listing_id"),
                ReadGuid(reader, "tenant_account_id"),
                ReadGuid(reader, "landlord_account_id"),
                Convert.ToInt64(reader["rent_minor"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["period_days"], CultureInfo.InvariantCulture),
                ReadTime(reader, "starts_at_utc_ticks"),
                ReadTime(reader, "ends_at_utc_ticks"),
                ReadTime(reader, "next_due_at_utc_ticks"),
                Convert.ToInt32(reader["active"], CultureInfo.InvariantCulture) != 0,
                ReadGuid(reader, "last_payment_transaction_id"));

        private NexLedgerAccount GetAccount(
            DbConnection connection,
            DbTransaction transaction,
            Guid accountId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT
                            account_id,
                            account_class,
                            normal_side,
                            account_reference,
                            display_name,
                            currency_code,
                            created_at_utc_ticks
                       FROM {AccountsTable}
                       WHERE account_id = @account_id");

            AddParameter(
                command,
                "@account_id",
                accountId.ToString("D"));

            using DbDataReader reader =
                command.ExecuteReader();

            return reader.Read()
                ? ReadAccount(reader)
                : null;
        }

        private NexLedgerAccount GetAccountByReference(
            DbConnection connection,
            DbTransaction transaction,
            string reference,
            string currencyCode)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT
                            account_id,
                            account_class,
                            normal_side,
                            account_reference,
                            display_name,
                            currency_code,
                            created_at_utc_ticks
                       FROM {AccountsTable}
                       WHERE account_reference = @account_reference
                         AND currency_code = @currency_code");

            AddParameter(
                command,
                "@account_reference",
                reference);
            AddParameter(
                command,
                "@currency_code",
                currencyCode);

            using DbDataReader reader =
                command.ExecuteReader();

            return reader.Read()
                ? ReadAccount(reader)
                : null;
        }

        private NexLedgerTransaction GetTransaction(
            DbConnection connection,
            DbTransaction transaction,
            Guid transactionId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT
                            transaction_id,
                            kind,
                            transaction_reference,
                            correlation_id,
                            currency_code,
                            occurred_at_utc_ticks,
                            metadata_json
                       FROM {TransactionsTable}
                       WHERE transaction_id = @transaction_id");

            AddParameter(
                command,
                "@transaction_id",
                transactionId.ToString("D"));

            Guid id;
            string kind;
            string reference;
            string correlationId;
            string currencyCode;
            long occurredTicks;
            string metadataJson;

            using (DbDataReader reader =
                   command.ExecuteReader())
            {
                if (!reader.Read())
                    return null;

                id =
                    Guid.Parse(
                        Convert.ToString(
                            reader["transaction_id"],
                            CultureInfo.InvariantCulture));
                kind =
                    Convert.ToString(
                        reader["kind"],
                        CultureInfo.InvariantCulture) ??
                    string.Empty;
                reference =
                    Convert.ToString(
                        reader["transaction_reference"],
                        CultureInfo.InvariantCulture) ??
                    string.Empty;
                correlationId =
                    Convert.ToString(
                        reader["correlation_id"],
                        CultureInfo.InvariantCulture) ??
                    string.Empty;
                currencyCode =
                    Convert.ToString(
                        reader["currency_code"],
                        CultureInfo.InvariantCulture) ??
                    string.Empty;
                occurredTicks =
                    Convert.ToInt64(
                        reader["occurred_at_utc_ticks"],
                        CultureInfo.InvariantCulture);
                metadataJson =
                    Convert.ToString(
                        reader["metadata_json"],
                        CultureInfo.InvariantCulture) ??
                    "{}";
            }

            List<NexLedgerPosting> postings =
                ReadTransactionPostings(
                    connection,
                    transaction,
                    transactionId);

            Dictionary<string, string> metadata =
                JsonSerializer.Deserialize<
                    Dictionary<string, string>>(
                        metadataJson) ??
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            return new NexLedgerTransaction(
                id,
                kind,
                reference,
                postings,
                correlationId,
                metadata,
                new DateTimeOffset(
                    new DateTime(
                        occurredTicks,
                        DateTimeKind.Utc)),
                currencyCode);
        }

        private List<NexLedgerPosting> ReadTransactionPostings(
            DbConnection connection,
            DbTransaction transaction,
            Guid transactionId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT
                            posting_id,
                            account_id,
                            side,
                            amount_minor,
                            memo
                       FROM {PostingsTable}
                       WHERE transaction_id = @transaction_id
                       ORDER BY sequence_no ASC");

            AddParameter(
                command,
                "@transaction_id",
                transactionId.ToString("D"));

            List<NexLedgerPosting> result =
                new List<NexLedgerPosting>();

            using DbDataReader reader =
                command.ExecuteReader();

            while (reader.Read())
                result.Add(ReadPosting(reader));

            return result;
        }

        private bool PostingExists(
            DbConnection connection,
            DbTransaction transaction,
            Guid postingId)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    $@"SELECT COUNT(*)
                       FROM {PostingsTable}
                       WHERE posting_id = @posting_id");

            AddParameter(
                command,
                "@posting_id",
                postingId.ToString("D"));

            object raw =
                command.ExecuteScalar();

            return Convert.ToInt64(
                       raw ?? 0,
                       CultureInfo.InvariantCulture) >
                   0;
        }

        private void InsertTransaction(
            DbConnection connection,
            DbTransaction dbTransaction,
            NexLedgerTransaction transaction)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    dbTransaction,
                    $@"INSERT INTO {TransactionsTable}
                        (transaction_id, kind, transaction_reference, correlation_id,
                         currency_code, occurred_at_utc_ticks, metadata_json)
                       VALUES
                        (@transaction_id, @kind, @transaction_reference, @correlation_id,
                         @currency_code, @occurred_at_utc_ticks, @metadata_json)");

            AddParameter(
                command,
                "@transaction_id",
                transaction.TransactionId.ToString("D"));
            AddParameter(
                command,
                "@kind",
                transaction.Kind);
            AddParameter(
                command,
                "@transaction_reference",
                transaction.Reference);
            AddParameter(
                command,
                "@correlation_id",
                transaction.CorrelationId);
            AddParameter(
                command,
                "@currency_code",
                transaction.CurrencyCode);
            AddParameter(
                command,
                "@occurred_at_utc_ticks",
                transaction.OccurredAt.UtcDateTime.Ticks);
            AddParameter(
                command,
                "@metadata_json",
                JsonSerializer.Serialize(
                    transaction.Metadata));

            command.ExecuteNonQuery();
        }

        private void InsertPosting(
            DbConnection connection,
            DbTransaction dbTransaction,
            Guid transactionId,
            int sequence,
            NexLedgerPosting posting)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    dbTransaction,
                    $@"INSERT INTO {PostingsTable}
                        (posting_id, transaction_id, sequence_no, account_id,
                         side, amount_minor, memo)
                       VALUES
                        (@posting_id, @transaction_id, @sequence_no, @account_id,
                         @side, @amount_minor, @memo)");

            AddParameter(
                command,
                "@posting_id",
                posting.PostingId.ToString("D"));
            AddParameter(
                command,
                "@transaction_id",
                transactionId.ToString("D"));
            AddParameter(
                command,
                "@sequence_no",
                sequence);
            AddParameter(
                command,
                "@account_id",
                posting.AccountId.ToString("D"));
            AddParameter(
                command,
                "@side",
                (int)posting.Side);
            AddParameter(
                command,
                "@amount_minor",
                posting.AmountMinor);
            AddParameter(
                command,
                "@memo",
                posting.Memo);

            command.ExecuteNonQuery();
        }

        private static NexLedgerAccount ReadAccount(
            DbDataReader reader)
        {
            return new NexLedgerAccount(
                Guid.Parse(
                    Convert.ToString(
                        reader["account_id"],
                        CultureInfo.InvariantCulture)),
                (NexLedgerAccountClass)
                    Convert.ToInt32(
                        reader["account_class"],
                        CultureInfo.InvariantCulture),
                (NexLedgerSide)
                    Convert.ToInt32(
                        reader["normal_side"],
                        CultureInfo.InvariantCulture),
                Convert.ToString(
                    reader["account_reference"],
                    CultureInfo.InvariantCulture) ??
                string.Empty,
                Convert.ToString(
                    reader["display_name"],
                    CultureInfo.InvariantCulture) ??
                string.Empty,
                Convert.ToString(
                    reader["currency_code"],
                    CultureInfo.InvariantCulture) ??
                NexLedgerCurrency.Code,
                new DateTimeOffset(
                    new DateTime(
                        Convert.ToInt64(
                            reader["created_at_utc_ticks"],
                            CultureInfo.InvariantCulture),
                        DateTimeKind.Utc)));
        }

        private static NexLedgerPosting ReadPosting(
            DbDataReader reader)
        {
            return new NexLedgerPosting(
                Guid.Parse(
                    Convert.ToString(
                        reader["posting_id"],
                        CultureInfo.InvariantCulture)),
                Guid.Parse(
                    Convert.ToString(
                        reader["account_id"],
                        CultureInfo.InvariantCulture)),
                (NexLedgerSide)
                    Convert.ToInt32(
                        reader["side"],
                        CultureInfo.InvariantCulture),
                Convert.ToInt64(
                    reader["amount_minor"],
                    CultureInfo.InvariantCulture),
                Convert.ToString(
                    reader["memo"],
                    CultureInfo.InvariantCulture) ??
                string.Empty);
        }

        private DbConnection OpenConnection()
        {
            DbConnection connection =
                m_ConnectionFactory();

            if (connection == null)
            {
                throw new InvalidOperationException(
                    "Ledger SQL connection factory returned null.");
            }

            connection.Open();

            if (m_Dialect ==
                NexLedgerSqlDialect.Sqlite)
            {
                ExecuteNonQuery(
                    connection,
                    null,
                    "PRAGMA foreign_keys = ON");
            }

            return connection;
        }

        private static DbCommand CreateCommand(
            DbConnection connection,
            DbTransaction transaction,
            string sql)
        {
            DbCommand command =
                connection.CreateCommand();

            command.CommandText =
                sql;
            command.CommandTimeout =
                30;

            if (transaction != null)
                command.Transaction = transaction;

            return command;
        }

        private static void AddParameter(
            DbCommand command,
            string name,
            object value)
        {
            DbParameter parameter =
                command.CreateParameter();

            parameter.ParameterName =
                name;
            parameter.Value =
                value ??
                DBNull.Value;

            command.Parameters.Add(
                parameter);
        }

        private static void ExecuteNonQuery(
            DbConnection connection,
            DbTransaction transaction,
            string sql)
        {
            using DbCommand command =
                CreateCommand(
                    connection,
                    transaction,
                    sql);

            command.ExecuteNonQuery();
        }

        private static void SafeRollback(
            DbTransaction transaction)
        {
            try
            {
                transaction?.Rollback();
            }
            catch
            {
            }
        }

        private static bool Equivalent(
            NexLedgerTransaction left,
            NexLedgerTransaction right)
        {
            if (left.TransactionId != right.TransactionId ||
                left.OccurredAt != right.OccurredAt ||
                !string.Equals(
                    left.Kind,
                    right.Kind,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    left.Reference,
                    right.Reference,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    left.CorrelationId,
                    right.CorrelationId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    left.CurrencyCode,
                    right.CurrencyCode,
                    StringComparison.Ordinal) ||
                left.Postings.Count != right.Postings.Count ||
                left.Metadata.Count != right.Metadata.Count)
            {
                return false;
            }

            for (int i = 0;
                 i < left.Postings.Count;
                 i++)
            {
                NexLedgerPosting a =
                    left.Postings[i];
                NexLedgerPosting b =
                    right.Postings[i];

                if (a.PostingId != b.PostingId ||
                    a.AccountId != b.AccountId ||
                    a.Side != b.Side ||
                    a.AmountMinor != b.AmountMinor ||
                    !string.Equals(
                        a.Memo,
                        b.Memo,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            foreach (KeyValuePair<string, string> item in
                     left.Metadata)
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
}
