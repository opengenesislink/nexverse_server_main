using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using NexVerse.Core.Economy;

internal static class Program
{
    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(
        Action action,
        string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static NexLedgerPosting Posting(
        Guid account,
        NexLedgerSide side,
        long amount,
        string memo) =>
        new NexLedgerPosting(
            Guid.NewGuid(),
            account,
            side,
            amount,
            memo);

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

    private static void VerifyProviderRollback(
        NexLedgerSqlRuntime runtime,
        string providerLabel)
    {
        Guid transactionId =
            Guid.NewGuid();

        using DbConnection connection =
            runtime.CreateConnection();
        connection.Open();

        using (DbTransaction transaction =
               connection.BeginTransaction(
                   IsolationLevel.Serializable))
        {
            using DbCommand insert =
                connection.CreateCommand();
            insert.Transaction =
                transaction;
            insert.CommandText =
                @"INSERT INTO ogl_ledger_transactions
                    (transaction_id, kind, transaction_reference, correlation_id,
                     currency_code, occurred_at_utc_ticks, metadata_json)
                  VALUES
                    (@transaction_id, @kind, @reference, @correlation_id,
                     @currency_code, @ticks, @metadata_json)";

            AddParameter(
                insert,
                "@transaction_id",
                transactionId.ToString("D"));
            AddParameter(
                insert,
                "@kind",
                "rollback-probe");
            AddParameter(
                insert,
                "@reference",
                "ROLLBACK-" +
                providerLabel);
            AddParameter(
                insert,
                "@correlation_id",
                "matrix-regression");
            AddParameter(
                insert,
                "@currency_code",
                NexLedgerCurrency.Code);
            AddParameter(
                insert,
                "@ticks",
                DateTime.UtcNow.Ticks);
            AddParameter(
                insert,
                "@metadata_json",
                "{}");

            Require(
                insert.ExecuteNonQuery() == 1,
                providerLabel +
                " rollback probe insert failed");

            transaction.Rollback();
        }

        using DbCommand verify =
            connection.CreateCommand();
        verify.CommandText =
            "SELECT COUNT(*) FROM ogl_ledger_transactions " +
            "WHERE transaction_id = @transaction_id";
        AddParameter(
            verify,
            "@transaction_id",
            transactionId.ToString("D"));

        long count =
            Convert.ToInt64(
                verify.ExecuteScalar(),
                CultureInfo.InvariantCulture);

        Require(
            count == 0,
            providerLabel +
            " rollback left a transaction header behind");
    }

    private static void VerifyProviderImplicitRollback(
        NexLedgerSqlRuntime runtime,
        string providerLabel)
    {
        Guid transactionId =
            Guid.NewGuid();

        DbConnection connection =
            runtime.CreateConnection();
        connection.Open();

        DbTransaction transaction =
            connection.BeginTransaction(
                IsolationLevel.Serializable);

        using (DbCommand insert =
               connection.CreateCommand())
        {
            insert.Transaction =
                transaction;
            insert.CommandText =
                @"INSERT INTO ogl_ledger_transactions
                    (transaction_id, kind, transaction_reference, correlation_id,
                     currency_code, occurred_at_utc_ticks, metadata_json)
                  VALUES
                    (@transaction_id, @kind, @reference, @correlation_id,
                     @currency_code, @ticks, @metadata_json)";

            AddParameter(insert, "@transaction_id", transactionId.ToString("D"));
            AddParameter(insert, "@kind", "connection-loss-probe");
            AddParameter(insert, "@reference", "CONNECTION-LOSS-" + providerLabel);
            AddParameter(insert, "@correlation_id", "matrix-regression");
            AddParameter(insert, "@currency_code", NexLedgerCurrency.Code);
            AddParameter(insert, "@ticks", DateTime.UtcNow.Ticks);
            AddParameter(insert, "@metadata_json", "{}");

            Require(
                insert.ExecuteNonQuery() == 1,
                providerLabel +
                " connection-loss probe insert failed");
        }

        // Simulate a process/connection loss: dispose the connection without
        // Commit() or explicit Rollback(). The provider/database must roll back
        // the open transaction.
        connection.Dispose();

        using DbConnection verification =
            runtime.CreateConnection();
        verification.Open();

        using DbCommand verify =
            verification.CreateCommand();
        verify.CommandText =
            "SELECT COUNT(*) FROM ogl_ledger_transactions " +
            "WHERE transaction_id = @transaction_id";
        AddParameter(
            verify,
            "@transaction_id",
            transactionId.ToString("D"));

        long count =
            Convert.ToInt64(
                verify.ExecuteScalar(),
                CultureInfo.InvariantCulture);

        Require(
            count == 0,
            providerLabel +
            " connection loss left an uncommitted transaction behind");
    }

    private static void RunProvider(
        string storageProvider,
        string connectionString,
        string expectedProvider)
    {
        NexLedgerSqlRuntime runtime =
            NexLedgerSqlRuntime.Resolve(
                storageProvider,
                connectionString);

        Require(
            runtime.ProviderName ==
                expectedProvider,
            expectedProvider +
            " provider resolution mismatch");

        NexLedgerSqlStore store =
            runtime.CreateStore();

        NexDoubleEntryLedger ledger =
            new NexDoubleEntryLedger(
                store);

        string suffix =
            Guid.NewGuid()
                .ToString("N");

        Guid systemId =
            Guid.NewGuid();
        Guid residentId =
            Guid.NewGuid();
        Guid merchantId =
            Guid.NewGuid();

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    systemId,
                    NexLedgerAccountClass.System,
                    NexLedgerSide.Debit,
                    "matrix:" +
                    expectedProvider +
                    ":system:" +
                    suffix,
                    expectedProvider +
                    " system")),
            expectedProvider +
            " system account was not created");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    residentId,
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "matrix:" +
                    expectedProvider +
                    ":resident:" +
                    suffix,
                    expectedProvider +
                    " resident")),
            expectedProvider +
            " resident account was not created");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    merchantId,
                    NexLedgerAccountClass.Business,
                    NexLedgerSide.Credit,
                    "matrix:" +
                    expectedProvider +
                    ":merchant:" +
                    suffix,
                    expectedProvider +
                    " merchant")),
            expectedProvider +
            " merchant account was not created");

        DateTimeOffset occurredAt =
            DateTimeOffset.UtcNow;

        NexLedgerTransaction issuance =
            new NexLedgerTransaction(
                Guid.NewGuid(),
                "issuance",
                "MATRIX-ISS-" +
                suffix,
                new[]
                {
                    Posting(
                        systemId,
                        NexLedgerSide.Debit,
                        1000,
                        "Issue 1000 NV$"),
                    Posting(
                        residentId,
                        NexLedgerSide.Credit,
                        1000,
                        "Resident credit")
                },
                "matrix-" +
                expectedProvider,
                null,
                occurredAt);

        NexLedgerTransaction payment =
            new NexLedgerTransaction(
                Guid.NewGuid(),
                "transfer",
                "MATRIX-PAY-" +
                suffix,
                new[]
                {
                    Posting(
                        residentId,
                        NexLedgerSide.Debit,
                        125,
                        "Resident payment"),
                    Posting(
                        merchantId,
                        NexLedgerSide.Credit,
                        125,
                        "Merchant receipt")
                },
                "matrix-" +
                expectedProvider,
                null,
                occurredAt.AddMilliseconds(1));

        Require(
            ledger.Post(issuance).Created,
            expectedProvider +
            " issuance was not created");
        Require(
            ledger.Post(payment).Created,
            expectedProvider +
            " payment was not created");

        Require(
            ledger.GetBalance(systemId) == 1000,
            expectedProvider +
            " system balance mismatch");
        Require(
            ledger.GetBalance(residentId) == 875,
            expectedProvider +
            " resident balance mismatch");
        Require(
            ledger.GetBalance(merchantId) == 125,
            expectedProvider +
            " merchant balance mismatch");

        NexDoubleEntryLedger reopened =
            new NexDoubleEntryLedger(
                runtime.CreateStore());

        Require(
            reopened.GetBalance(residentId) == 875,
            expectedProvider +
            " reopened resident balance mismatch");
        Require(
            reopened.Post(issuance).Status ==
                NexLedgerAppendStatus.Duplicate,
            expectedProvider +
            " identical retry was not idempotent");
        Require(
            reopened.ListPostings(
                residentId,
                0,
                10).Count == 2,
            expectedProvider +
            " posting history mismatch");

        RequireThrows<NexLedgerConflictException>(
            () => reopened.TryCreateAccount(
                new NexLedgerAccount(
                    Guid.NewGuid(),
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "matrix:" +
                    expectedProvider +
                    ":resident:" +
                    suffix,
                    "duplicate reference")),
            expectedProvider +
            " duplicate reference was accepted");

        long balanceBefore =
            reopened.GetBalance(
                residentId);

        NexLedgerTransaction invalid =
            new NexLedgerTransaction(
                Guid.NewGuid(),
                "transfer",
                "MATRIX-BAD-" +
                suffix,
                new[]
                {
                    Posting(
                        residentId,
                        NexLedgerSide.Debit,
                        1,
                        "known"),
                    Posting(
                        Guid.NewGuid(),
                        NexLedgerSide.Credit,
                        1,
                        "unknown")
                });

        RequireThrows<NexLedgerValidationException>(
            () => reopened.Post(invalid),
            expectedProvider +
            " unknown-account transaction was accepted");

        Require(
            reopened.GetBalance(residentId) ==
                balanceBefore,
            expectedProvider +
            " rejected transaction changed balance");

        VerifyProviderRollback(
            runtime,
            expectedProvider);

        VerifyProviderImplicitRollback(
            runtime,
            expectedProvider);

        using DbConnection connection =
            runtime.CreateConnection();
        connection.Open();

        using DbCommand version =
            connection.CreateCommand();
        version.CommandText =
            "SELECT MAX(version) FROM ogl_ledger_schema";

        Require(
            Convert.ToInt32(
                version.ExecuteScalar(),
                CultureInfo.InvariantCulture) ==
                NexLedgerSqlStore.CurrentSchemaVersion,
            expectedProvider +
            " schema version mismatch");

        Console.WriteLine(
            expectedProvider +
            " live NV$ ledger regression: OK");
    }

    private static int Main()
    {
        string mariaDb =
            Environment.GetEnvironmentVariable(
                "NEX_LEDGER_MARIADB") ??
            string.Empty;
        string postgreSql =
            Environment.GetEnvironmentVariable(
                "NEX_LEDGER_POSTGRESQL") ??
            string.Empty;

        Require(
            mariaDb.Length > 0,
            "NEX_LEDGER_MARIADB is required");
        Require(
            postgreSql.Length > 0,
            "NEX_LEDGER_POSTGRESQL is required");

        RunProvider(
            "OpenSim.Data.MySQL.dll",
            mariaDb,
            "mysql");

        RunProvider(
            "OpenSim.Data.PGSQL.dll",
            postgreSql,
            "postgresql");

        Console.WriteLine(
            "OpenGenesisLINK NV$ live database matrix: OK");
        return 0;
    }
}
