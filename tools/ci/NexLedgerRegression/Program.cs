using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
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
        string id,
        Guid account,
        NexLedgerSide side,
        long amount,
        string memo) =>
        new NexLedgerPosting(
            Guid.Parse(id),
            account,
            side,
            amount,
            memo);

    private static NexLedgerPosting[] BuildTooManyPostings(
        Guid debitAccount,
        Guid creditAccount)
    {
        int pairCount =
            NexLedgerTransaction.PostingCountLimit / 2 + 1;
        List<NexLedgerPosting> postings =
            new List<NexLedgerPosting>();

        for (int i = 0; i < pairCount; i++)
        {
            postings.Add(
                new NexLedgerPosting(
                    Guid.NewGuid(),
                    debitAccount,
                    NexLedgerSide.Debit,
                    1));
            postings.Add(
                new NexLedgerPosting(
                    Guid.NewGuid(),
                    creditAccount,
                    NexLedgerSide.Credit,
                    1));
        }

        return postings.ToArray();
    }

    private static void RunSqliteRegression()
    {
        string databasePath =
            Path.Combine(
                Path.GetTempPath(),
                "ogl-ledger-" +
                Guid.NewGuid().ToString("N") +
                ".db");

        NexLedgerSqlRuntime runtime =
            NexLedgerSqlRuntime.Resolve(
                "OpenSim.Data.SQLite.dll",
                "URI=file:" +
                databasePath +
                ",version=3");

        Func<DbConnection> connectionFactory =
            runtime.CreateConnection;

        Guid systemId =
            Guid.Parse("B0000000-0000-0000-0000-000000000001");
        Guid residentId =
            Guid.Parse("B0000000-0000-0000-0000-000000000002");
        Guid merchantId =
            Guid.Parse("B0000000-0000-0000-0000-000000000003");

        DateTimeOffset occurredAt =
            new DateTimeOffset(
                2026, 10, 5, 21, 0, 0,
                TimeSpan.Zero);

        NexLedgerTransaction issuance =
            new NexLedgerTransaction(
                Guid.Parse("B1000000-0000-0000-0000-000000000001"),
                "issuance",
                "SQL-ISS-0001",
                new[]
                {
                    new NexLedgerPosting(
                        Guid.Parse("B1100000-0000-0000-0000-000000000001"),
                        systemId,
                        NexLedgerSide.Debit,
                        250,
                        "Issue NV$"),
                    new NexLedgerPosting(
                        Guid.Parse("B1100000-0000-0000-0000-000000000002"),
                        residentId,
                        NexLedgerSide.Credit,
                        250,
                        "Resident credit")
                },
                "sqlite-regression",
                new Dictionary<string, string>
                {
                    ["source"] = "ci"
                },
                occurredAt);

        NexLedgerTransaction payment =
            new NexLedgerTransaction(
                Guid.Parse("B2000000-0000-0000-0000-000000000001"),
                "transfer",
                "SQL-PAY-0001",
                new[]
                {
                    new NexLedgerPosting(
                        Guid.Parse("B2100000-0000-0000-0000-000000000001"),
                        residentId,
                        NexLedgerSide.Debit,
                        75,
                        "Resident payment"),
                    new NexLedgerPosting(
                        Guid.Parse("B2100000-0000-0000-0000-000000000002"),
                        merchantId,
                        NexLedgerSide.Credit,
                        75,
                        "Merchant receipt")
                },
                "sqlite-regression",
                null,
                occurredAt.AddMinutes(1));

        try
        {
            Require(
                runtime.ProviderName == "sqlite" &&
                runtime.Dialect == NexLedgerSqlDialect.Sqlite,
                "SQL runtime provider resolution mismatch");

            NexLedgerSqlStore store =
                runtime.CreateStore();

            NexDoubleEntryLedger ledger =
                new NexDoubleEntryLedger(
                    store);

            Require(
                ledger.TryCreateAccount(
                    new NexLedgerAccount(
                        systemId,
                        NexLedgerAccountClass.System,
                        NexLedgerSide.Debit,
                        "system:sql-issuance",
                        "SQL NV$ Issuance")),
                "SQL system account was not created");

            Require(
                ledger.TryCreateAccount(
                    new NexLedgerAccount(
                        residentId,
                        NexLedgerAccountClass.Resident,
                        NexLedgerSide.Credit,
                        "resident:sql-demo",
                        "SQL Demo Resident")),
                "SQL resident account was not created");

            Require(
                ledger.TryCreateAccount(
                    new NexLedgerAccount(
                        merchantId,
                        NexLedgerAccountClass.Business,
                        NexLedgerSide.Credit,
                        "business:sql-demo",
                        "SQL Demo Merchant")),
                "SQL merchant account was not created");

            Require(
                ledger.Post(issuance).Created,
                "SQL issuance was not created");

            Require(
                ledger.Post(payment).Created,
                "SQL transfer was not created");

            Require(
                ledger.GetBalance(systemId) == 250,
                "SQL system balance mismatch");
            Require(
                ledger.GetBalance(residentId) == 175,
                "SQL resident balance mismatch");
            Require(
                ledger.GetBalance(merchantId) == 75,
                "SQL merchant balance mismatch");

            NexLedgerSqlStore reopened =
                runtime.CreateStore();

            NexDoubleEntryLedger reopenedLedger =
                new NexDoubleEntryLedger(
                    reopened);

            Require(
                reopenedLedger.GetBalance(residentId) == 175,
                "SQL balance did not survive store reopen");

            NexLedgerTransaction loaded =
                reopenedLedger.GetTransaction(
                    issuance.TransactionId);

            Require(
                loaded != null &&
                loaded.Reference == issuance.Reference &&
                loaded.Postings.Count == 2 &&
                loaded.Metadata["source"] == "ci",
                "SQL transaction reconstruction mismatch");

            Require(
                reopenedLedger.Post(issuance).Status ==
                    NexLedgerAppendStatus.Duplicate,
                "SQL identical retry was not idempotent");

            Require(
                reopenedLedger.ListPostings(
                    residentId,
                    0,
                    10).Count == 2,
                "SQL resident posting history mismatch");

            Require(
                !reopenedLedger.TryCreateAccount(
                    new NexLedgerAccount(
                        residentId,
                        NexLedgerAccountClass.Resident,
                        NexLedgerSide.Credit,
                        "resident:sql-other",
                        "Duplicate SQL ID")),
                "SQL duplicate account ID was accepted");

            RequireThrows<NexLedgerConflictException>(
                () => reopenedLedger.TryCreateAccount(
                    new NexLedgerAccount(
                        Guid.NewGuid(),
                        NexLedgerAccountClass.Resident,
                        NexLedgerSide.Credit,
                        "resident:sql-demo",
                        "Duplicate SQL reference")),
                "SQL duplicate account reference was accepted");

            NexLedgerTransaction invalid =
                new NexLedgerTransaction(
                    Guid.Parse("B3000000-0000-0000-0000-000000000001"),
                    "transfer",
                    "SQL-BAD-ACCOUNT",
                    new[]
                    {
                        new NexLedgerPosting(
                            Guid.Parse("B3100000-0000-0000-0000-000000000001"),
                            residentId,
                            NexLedgerSide.Debit,
                            1),
                        new NexLedgerPosting(
                            Guid.Parse("B3100000-0000-0000-0000-000000000002"),
                            Guid.Parse("B3000000-0000-0000-0000-000000000099"),
                            NexLedgerSide.Credit,
                            1)
                    });

            RequireThrows<NexLedgerValidationException>(
                () => reopenedLedger.Post(invalid),
                "SQL transaction with unknown account was accepted");

            Require(
                reopenedLedger.GetBalance(residentId) == 175,
                "rejected SQL transaction changed resident balance");

            using DbConnection schemaConnection =
                connectionFactory();
            schemaConnection.Open();

            using DbCommand schemaCommand =
                schemaConnection.CreateCommand();
            schemaCommand.CommandText =
                "SELECT MAX(version) FROM ogl_ledger_schema";

            Require(
                Convert.ToInt32(
                    schemaCommand.ExecuteScalar()) ==
                    NexLedgerSqlStore.CurrentSchemaVersion,
                "SQL ledger schema version mismatch");
        }
        finally
        {
            foreach (string suffix in
                     new[] { string.Empty, "-journal", "-wal", "-shm" })
            {
                string path =
                    databasePath +
                    suffix;

                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    private static int Main()
    {
        Guid systemId =
            Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid residentId =
            Guid.Parse("20000000-0000-0000-0000-000000000002");
        Guid merchantId =
            Guid.Parse("30000000-0000-0000-0000-000000000003");

        InMemoryNexLedgerStore store =
            new InMemoryNexLedgerStore();
        NexDoubleEntryLedger ledger =
            new NexDoubleEntryLedger(store);

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    systemId,
                    NexLedgerAccountClass.System,
                    NexLedgerSide.Debit,
                    "system:issuance",
                    "NV$ Issuance")),
            "system ledger account was not created");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    residentId,
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "resident:demo",
                    "Demo Resident")),
            "resident ledger account was not created");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    merchantId,
                    NexLedgerAccountClass.Business,
                    NexLedgerSide.Credit,
                    "business:demo",
                    "Demo Merchant")),
            "merchant ledger account was not created");

        RequireThrows<ArgumentOutOfRangeException>(
            () => new NexLedgerAccount(
                Guid.NewGuid(),
                (NexLedgerAccountClass)999,
                NexLedgerSide.Credit,
                "invalid:class",
                "Invalid Class"),
            "undefined ledger account class was accepted");

        RequireThrows<ArgumentOutOfRangeException>(
            () => new NexLedgerPosting(
                Guid.NewGuid(),
                residentId,
                NexLedgerSide.Debit,
                1,
                new string('x', NexLedgerPosting.MemoLengthLimit + 1)),
            "oversized posting memo was accepted");

        Require(
            !ledger.TryCreateAccount(
                new NexLedgerAccount(
                    residentId,
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "resident:other",
                    "Duplicate ID")),
            "duplicate account ID was accepted");

        RequireThrows<NexLedgerConflictException>(
            () => ledger.TryCreateAccount(
                new NexLedgerAccount(
                    Guid.Parse("40000000-0000-0000-0000-000000000004"),
                    NexLedgerAccountClass.Resident,
                    NexLedgerSide.Credit,
                    "resident:demo",
                    "Duplicate reference")),
            "duplicate account reference was accepted");

        DateTimeOffset issuedAt =
            new DateTimeOffset(
                2026, 10, 5, 20, 0, 0,
                TimeSpan.Zero);

        Dictionary<string, string> metadata =
            new Dictionary<string, string>
            {
                ["reason"] = "initial-credit"
            };

        NexLedgerTransaction issuance =
            new NexLedgerTransaction(
                Guid.Parse("50000000-0000-0000-0000-000000000005"),
                "issuance",
                "ISS-0001",
                new[]
                {
                    Posting(
                        "51000000-0000-0000-0000-000000000001",
                        systemId,
                        NexLedgerSide.Debit,
                        100,
                        "Issue 100 NV$"),
                    Posting(
                        "51000000-0000-0000-0000-000000000002",
                        residentId,
                        NexLedgerSide.Credit,
                        100,
                        "Credit resident")
                },
                "ledger-regression",
                metadata,
                issuedAt);

        metadata["reason"] = "mutated-after-construction";
        Require(
            issuance.Metadata["reason"] == "initial-credit",
            "transaction metadata was not copied immutably");

        NexLedgerAppendResult issued =
            ledger.Post(issuance);

        Require(issued.Created, "issuance was not created");
        Require(ledger.GetBalance(systemId) == 100,
            "debit-normal system balance mismatch");
        Require(ledger.GetBalance(residentId) == 100,
            "credit-normal resident balance mismatch");
        Require(ledger.GetBalance(merchantId) == 0,
            "new merchant balance must be zero");

        NexLedgerAppendResult duplicate =
            ledger.Post(issuance);
        Require(
            duplicate.Status == NexLedgerAppendStatus.Duplicate,
            "identical transaction retry must be idempotent");
        Require(
            ledger.GetBalance(residentId) == 100,
            "idempotent retry changed resident balance");

        NexLedgerTransaction payment =
            new NexLedgerTransaction(
                Guid.Parse("60000000-0000-0000-0000-000000000006"),
                "transfer",
                "PAY-0001",
                new[]
                {
                    Posting(
                        "61000000-0000-0000-0000-000000000001",
                        residentId,
                        NexLedgerSide.Debit,
                        30,
                        "Resident payment"),
                    Posting(
                        "61000000-0000-0000-0000-000000000002",
                        merchantId,
                        NexLedgerSide.Credit,
                        30,
                        "Merchant receipt")
                },
                "ledger-regression",
                null,
                issuedAt.AddMinutes(1));

        Require(
            ledger.Post(payment).Created,
            "resident-to-merchant transfer was not posted");
        Require(ledger.GetBalance(residentId) == 70,
            "resident balance after transfer mismatch");
        Require(ledger.GetBalance(merchantId) == 30,
            "merchant balance after transfer mismatch");

        Require(
            ledger.ListPostings(residentId, 0, 10).Count == 2,
            "resident posting history mismatch");
        Require(
            ledger.ListPostings(residentId, 1, 1).Count == 1,
            "resident posting pagination mismatch");

        RequireThrows<NexLedgerValidationException>(
            () => new NexLedgerTransaction(
                Guid.NewGuid(),
                "too-many-postings",
                "BAD-COUNT",
                BuildTooManyPostings(residentId, merchantId)),
            "oversized posting collection was accepted");

        Dictionary<string, string> oversizedMetadata =
            new Dictionary<string, string>();
        for (int i = 0; i <= NexLedgerTransaction.MetadataEntryLimit; i++)
            oversizedMetadata["k" + i] = "v";

        RequireThrows<ArgumentOutOfRangeException>(
            () => new NexLedgerTransaction(
                Guid.NewGuid(),
                "metadata",
                "BAD-METADATA",
                new[]
                {
                    new NexLedgerPosting(Guid.NewGuid(), residentId, NexLedgerSide.Debit, 1),
                    new NexLedgerPosting(Guid.NewGuid(), merchantId, NexLedgerSide.Credit, 1)
                },
                metadata: oversizedMetadata),
            "oversized metadata collection was accepted");

        RequireThrows<NexLedgerValidationException>(
            () => new NexLedgerTransaction(
                Guid.Parse("70000000-0000-0000-0000-000000000007"),
                "invalid",
                "BAD-UNBALANCED",
                new[]
                {
                    Posting(
                        "71000000-0000-0000-0000-000000000001",
                        residentId,
                        NexLedgerSide.Debit,
                        10,
                        "bad"),
                    Posting(
                        "71000000-0000-0000-0000-000000000002",
                        merchantId,
                        NexLedgerSide.Credit,
                        9,
                        "bad")
                }),
            "unbalanced transaction was accepted");

        Guid missingAccount =
            Guid.Parse("80000000-0000-0000-0000-000000000008");
        NexLedgerTransaction unknownAccount =
            new NexLedgerTransaction(
                Guid.Parse("81000000-0000-0000-0000-000000000008"),
                "invalid",
                "BAD-ACCOUNT",
                new[]
                {
                    Posting(
                        "82000000-0000-0000-0000-000000000001",
                        residentId,
                        NexLedgerSide.Debit,
                        1,
                        "known"),
                    Posting(
                        "82000000-0000-0000-0000-000000000002",
                        missingAccount,
                        NexLedgerSide.Credit,
                        1,
                        "missing")
                });

        RequireThrows<NexLedgerValidationException>(
            () => ledger.Post(unknownAccount),
            "transaction with unknown account was accepted");
        Require(ledger.GetBalance(residentId) == 70,
            "failed transaction changed resident balance");

        NexLedgerTransaction postingCollision =
            new NexLedgerTransaction(
                Guid.Parse("90000000-0000-0000-0000-000000000009"),
                "invalid",
                "BAD-POSTING-ID",
                new[]
                {
                    Posting(
                        "61000000-0000-0000-0000-000000000001",
                        residentId,
                        NexLedgerSide.Debit,
                        1,
                        "reused posting id"),
                    Posting(
                        "91000000-0000-0000-0000-000000000002",
                        merchantId,
                        NexLedgerSide.Credit,
                        1,
                        "new posting id")
                });

        RequireThrows<NexLedgerConflictException>(
            () => ledger.Post(postingCollision),
            "global posting ID collision was accepted");
        Require(
            ledger.GetBalance(residentId) == 70 &&
            ledger.GetBalance(merchantId) == 30,
            "posting collision changed balances");

        NexLedgerTransaction sameIdDifferentContent =
            new NexLedgerTransaction(
                issuance.TransactionId,
                "issuance",
                "ISS-CHANGED",
                new[]
                {
                    Posting(
                        "A1000000-0000-0000-0000-000000000001",
                        systemId,
                        NexLedgerSide.Debit,
                        1,
                        "changed"),
                    Posting(
                        "A1000000-0000-0000-0000-000000000002",
                        residentId,
                        NexLedgerSide.Credit,
                        1,
                        "changed")
                },
                issuance.CorrelationId,
                null,
                issuance.OccurredAt);

        RequireThrows<NexLedgerConflictException>(
            () => ledger.Post(sameIdDifferentContent),
            "transaction ID reuse with different content was accepted");

        Require(NexLedgerCurrency.Symbol == "NV$",
            "NV$ display symbol contract mismatch");
        Require(NexLedgerCurrency.Code == "NVD",
            "NVD internal currency code contract mismatch");
        Require(NexLedgerCurrency.MinorUnits == 0,
            "NV$ must initially use integer units");

        RunSqliteRegression();

        Console.WriteLine("OpenGenesisLINK NV$ double-entry ledger regression: OK");
        return 0;
    }
}
