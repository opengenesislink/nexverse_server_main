using System;
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

    private static void RunPolicyRegression(
        INexLedgerStore store,
        INexLedgerAccountStateStore stateStore,
        string label)
    {
        NexDoubleEntryLedger ledger =
            new NexDoubleEntryLedger(store);
        NexEconomyService economy =
            new NexEconomyService(
                ledger,
                stateStore);

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
                    "system:" + label + ":" + systemId.ToString("N"),
                    label + " issuance")),
            label + ": system account not created");

        NexLedgerAccount resident =
            economy.EnsureResidentAccount(
                residentId,
                label + " resident");

        Require(
            resident.AccountId == residentId &&
            resident.AccountClass == NexLedgerAccountClass.Resident,
            label + ": resident account mapping mismatch");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    merchantId,
                    NexLedgerAccountClass.Business,
                    NexLedgerSide.Credit,
                    "business:" + label + ":" + merchantId.ToString("N"),
                    label + " merchant")),
            label + ": merchant account not created");

        economy.AdministrativeAdjustment(
            residentId,
            systemId,
            1000,
            "ci-admin",
            "initial funding");

        Require(
            economy.GetBalance(residentId) == 1000,
            label + ": initial resident funding mismatch");

        Guid transferId =
            Guid.NewGuid();

        NexLedgerAppendResult transfer =
            economy.Transfer(
                residentId,
                merchantId,
                250,
                "POLICY-PAYMENT",
                "policy-regression",
                transferId);

        Require(
            transfer.Created,
            label + ": initial transfer not created");

        Require(
            economy.Transfer(
                residentId,
                merchantId,
                250,
                "POLICY-PAYMENT",
                "policy-regression",
                transferId).Status ==
                NexLedgerAppendStatus.Duplicate,
            label + ": identical transfer retry was not idempotent");

        RequireThrows<NexLedgerConflictException>(
            () => economy.Transfer(
                residentId,
                merchantId,
                249,
                "POLICY-PAYMENT",
                "policy-regression",
                transferId),
            label + ": transaction ID reuse with different amount was accepted");

        Require(
            economy.GetBalance(residentId) == 750 &&
            economy.GetBalance(merchantId) == 250,
            label + ": transfer balances mismatch");

        NexLedgerAccountState locked =
            economy.SetAccountStatus(
                residentId,
                NexLedgerAccountStatus.Locked,
                "ci-admin",
                "fraud review");

        Require(
            locked.Status == NexLedgerAccountStatus.Locked &&
            locked.Version == 1,
            label + ": resident lock state mismatch");

        RequireThrows<NexLedgerPolicyException>(
            () => economy.Transfer(
                residentId,
                merchantId,
                1,
                "LOCKED-TRANSFER"),
            label + ": locked resident was allowed to transfer");

        NexLedgerAppendResult reversal =
            economy.Reverse(
                transferId,
                "ci-admin",
                "payment rollback");

        Require(
            reversal.Created,
            label + ": reversal not created");
        Require(
            economy.GetBalance(residentId) == 1000 &&
            economy.GetBalance(merchantId) == 0,
            label + ": reversal balances mismatch");

        Require(
            economy.Reverse(
                transferId,
                "ci-admin",
                "payment rollback").Status ==
                NexLedgerAppendStatus.Duplicate,
            label + ": reversal retry was not idempotent");

        NexLedgerAccountState unlocked =
            economy.SetAccountStatus(
                residentId,
                NexLedgerAccountStatus.Active,
                "ci-admin",
                "review complete");

        Require(
            unlocked.Status == NexLedgerAccountStatus.Active &&
            unlocked.Version == 2,
            label + ": resident unlock state mismatch");

        economy.Transfer(
            residentId,
            merchantId,
            100,
            "POST-UNLOCK");

        Require(
            economy.GetBalance(residentId) == 900 &&
            economy.GetBalance(merchantId) == 100,
            label + ": post-unlock transfer mismatch");

        RequireThrows<NexLedgerPolicyException>(
            () => economy.SetAccountStatus(
                merchantId,
                NexLedgerAccountStatus.Closed,
                "ci-admin",
                "close with balance"),
            label + ": non-zero merchant account was closed");

        economy.AdministrativeAdjustment(
            merchantId,
            systemId,
            -100,
            "ci-admin",
            "settle before close");

        Require(
            economy.GetBalance(merchantId) == 0,
            label + ": merchant settlement mismatch");

        NexLedgerAccountState closed =
            economy.SetAccountStatus(
                merchantId,
                NexLedgerAccountStatus.Closed,
                "ci-admin",
                "merchant retired");

        Require(
            closed.Status == NexLedgerAccountStatus.Closed,
            label + ": merchant close failed");

        RequireThrows<NexLedgerPolicyException>(
            () => economy.Transfer(
                residentId,
                merchantId,
                1,
                "CLOSED-DEST"),
            label + ": transfer to closed account was accepted");

        RequireThrows<NexLedgerPolicyException>(
            () => economy.SetAccountStatus(
                merchantId,
                NexLedgerAccountStatus.Active,
                "ci-admin",
                "illegal reopen"),
            label + ": closed account was reopened");

        Require(
            economy.ListAccountStateEvents(
                residentId,
                0,
                10).Count == 2,
            label + ": resident lifecycle event count mismatch");

        Require(
            economy.ListAccountStateEvents(
                merchantId,
                0,
                10).Count == 1,
            label + ": merchant lifecycle event count mismatch");

        string maxReason =
            new string(
                'R',
                NexLedgerAccountState.ReasonLengthLimit);

        economy.AdministrativeAdjustment(
            residentId,
            systemId,
            1,
            "ci-admin",
            maxReason);

        economy.AdministrativeAdjustment(
            residentId,
            systemId,
            -1,
            "ci-admin",
            maxReason);

        Require(
            economy.GetBalance(residentId) == 900,
            label + ": long-reason adjustment roundtrip mismatch");
    }

    private static void RunSqlitePersistence()
    {
        string path =
            Path.Combine(
                Path.GetTempPath(),
                "ogl-policy-" +
                Guid.NewGuid().ToString("N") +
                ".db");

        try
        {
            NexLedgerSqlRuntime runtime =
                NexLedgerSqlRuntime.Resolve(
                    "OpenSim.Data.SQLite.dll",
                    "URI=file:" +
                    path +
                    ",version=3");

            NexLedgerSqlStore store =
                runtime.CreateStore();

            RunPolicyRegression(
                store,
                store,
                "sqlite");

            using DbConnection connection =
                runtime.CreateConnection();
            connection.Open();

            using DbCommand version =
                connection.CreateCommand();
            version.CommandText =
                "SELECT MAX(version) FROM ogl_ledger_schema";

            Require(
                Convert.ToInt32(
                    version.ExecuteScalar()) ==
                    NexLedgerSqlStore.CurrentSchemaVersion,
                "sqlite: schema version mismatch");

            using DbCommand events =
                connection.CreateCommand();
            events.CommandText =
                "SELECT COUNT(*) FROM ogl_ledger_account_events";

            Require(
                Convert.ToInt64(
                    events.ExecuteScalar()) >= 3,
                "sqlite: lifecycle events were not persisted");

            NexLedgerSqlStore reopened =
                runtime.CreateStore();

            using DbCommand stateRows =
                connection.CreateCommand();
            stateRows.CommandText =
                "SELECT COUNT(*) FROM ogl_ledger_account_state";

            Require(
                Convert.ToInt64(
                    stateRows.ExecuteScalar()) >= 3,
                "sqlite: account state rows were not persisted");

            Require(
                reopened != null,
                "sqlite: ledger store did not reopen");
        }
        finally
        {
            foreach (string suffix in
                     new[] { string.Empty, "-journal", "-wal", "-shm" })
            {
                string candidate =
                    path + suffix;

                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    private static int Main()
    {
        InMemoryNexLedgerStore memory =
            new InMemoryNexLedgerStore();

        RunPolicyRegression(
            memory,
            memory,
            "memory");

        RunSqlitePersistence();

        Console.WriteLine(
            "OpenGenesisLINK NV$ account lifecycle and policy regression: OK");

        return 0;
    }
}
