using System;
using System.Collections.Generic;
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

        Console.WriteLine("OpenGenesisLINK NV$ double-entry ledger regression: OK");
        return 0;
    }
}
