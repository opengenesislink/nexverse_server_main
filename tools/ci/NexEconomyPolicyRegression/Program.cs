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

        NexLedgerAccount system =
            economy.EnsureSystemAccount(
                systemId,
                label + " issuance");

        Require(
            system.AccountId == systemId &&
            system.AccountClass == NexLedgerAccountClass.System &&
            system.NormalSide == NexLedgerSide.Debit,
            label + ": system account mapping mismatch");

        Require(
            economy.EnsureSystemAccount(
                systemId,
                label + " issuance").AccountId ==
                systemId,
            label + ": system account ensure is not idempotent");

        NexLedgerAccount resident =
            economy.EnsureResidentAccount(
                residentId,
                label + " resident");

        Require(
            resident.AccountId == residentId &&
            resident.AccountClass == NexLedgerAccountClass.Resident,
            label + ": resident account mapping mismatch");

        NexVirtualBankAccount residentVirtual =
            economy.EnsureVirtualBankAccount(residentId);

        Require(
            NexVirtualBankAccount.IsValidIdentifier(residentVirtual.Identifier) &&
            residentVirtual.Identifier.StartsWith(
                NexVirtualBankAccount.Prefix,
                StringComparison.Ordinal) &&
            residentVirtual.AccountId == residentId,
            label + ": resident NVBAN mapping mismatch");

        Require(
            economy.EnsureVirtualBankAccount(residentId).Identifier ==
                residentVirtual.Identifier,
            label + ": resident NVBAN assignment is not idempotent");

        Require(
            economy.ResolveVirtualBankAccount(
                residentVirtual.Identifier)?.AccountId ==
                residentId,
            label + ": resident NVBAN reverse lookup mismatch");

        RequireThrows<ArgumentException>(
            () => economy.ResolveVirtualBankAccount(
                "DE00-THIS-IS-NOT-AN-NVBAN"),
            label + ": real-world-like bank identifier was accepted as NVBAN");

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    merchantId,
                    NexLedgerAccountClass.Business,
                    NexLedgerSide.Credit,
                    "business:" + label + ":" + merchantId.ToString("N"),
                    label + " merchant")),
            label + ": merchant account not created");

        NexVirtualBankAccount merchantVirtual =
            economy.EnsureVirtualBankAccount(merchantId);

        Require(
            NexVirtualBankAccount.IsValidIdentifier(merchantVirtual.Identifier) &&
            merchantVirtual.Identifier != residentVirtual.Identifier,
            label + ": merchant NVBAN assignment mismatch");

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

    private static void RunBankingCommerceRegression(
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

        Guid systemId = Guid.NewGuid();
        Guid payerId = Guid.NewGuid();
        Guid merchantId = Guid.NewGuid();
        Guid feeId = Guid.NewGuid();
        Guid escrowId = Guid.NewGuid();

        Require(
            ledger.TryCreateAccount(
                new NexLedgerAccount(
                    systemId,
                    NexLedgerAccountClass.System,
                    NexLedgerSide.Debit,
                    "system:banking:" + label + ":" + systemId.ToString("N"),
                    label + " banking issuance")),
            label + ": banking system account not created");

        economy.EnsureResidentAccount(
            payerId,
            label + " banking payer");
        economy.EnsureWalletAccount(
            merchantId,
            NexLedgerAccountClass.Business,
            label + " merchant");
        economy.EnsureWalletAccount(
            feeId,
            NexLedgerAccountClass.Business,
            label + " fee wallet");

        economy.AdministrativeAdjustment(
            payerId,
            systemId,
            5000,
            "ci-admin",
            "banking regression funding");

        economy.SetTransferPolicy(
            new NexAccountTransferPolicy(
                payerId,
                1000,
                5000,
                5,
                feeId));

        Guid bankingTransactionId =
            Guid.NewGuid();

        NexLedgerAppendResult banking =
            economy.BankingTransfer(
                payerId,
                merchantId,
                100,
                "BANK-TRANSFER",
                label,
                bankingTransactionId);

        Require(
            banking.Created &&
            economy.GetBalance(payerId) == 4895 &&
            economy.GetBalance(merchantId) == 100 &&
            economy.GetBalance(feeId) == 5,
            label + ": banking transfer/fee balances mismatch");

        Require(
            economy.BankingTransfer(
                payerId,
                merchantId,
                100,
                "BANK-TRANSFER",
                label,
                bankingTransactionId).Status ==
                NexLedgerAppendStatus.Duplicate &&
            economy.GetBalance(payerId) == 4895,
            label + ": banking transfer retry was not idempotent");

        RequireThrows<NexLedgerPolicyException>(
            () => economy.BankingTransfer(
                payerId,
                merchantId,
                1001,
                "LIMIT-REJECT"),
            label + ": per-transfer limit was not enforced");

        NexPaymentRequest request =
            economy.CreatePaymentRequest(
                merchantId,
                payerId,
                200,
                "PAYMENT-REQUEST");

        NexPaymentRequest paid =
            economy.PayPaymentRequest(
                request.RequestId,
                payerId,
                label);

        Require(
            paid.Status == NexPaymentRequestStatus.Paid &&
            paid.PaymentTransactionId != Guid.Empty &&
            economy.GetBalance(payerId) == 4690 &&
            economy.GetBalance(merchantId) == 300 &&
            economy.GetBalance(feeId) == 10,
            label + ": payment request settlement mismatch");

        NexBankStatement statement =
            economy.GetStatement(
                payerId,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));

        Require(
            statement.OpeningBalance == 0 &&
            statement.ClosingBalance == 4690 &&
            statement.Transactions.Count >= 3,
            label + ": statement balances/history mismatch");

        NexReconciliationReport reconciliation =
            economy.ReconcileAccount(
                payerId);

        Require(
            reconciliation.Balanced &&
            reconciliation.DerivedBalance ==
                reconciliation.RecomputedBalance,
            label + ": reconciliation failed");

        economy.FundEscrow(
            payerId,
            escrowId,
            300,
            "ESCROW-FUND",
            label,
            Guid.NewGuid());

        economy.ReleaseEscrow(
            escrowId,
            merchantId,
            100,
            "ESCROW-RELEASE",
            label,
            Guid.NewGuid());

        Require(
            economy.GetBalance(escrowId) == 200 &&
            economy.GetBalance(payerId) == 4390 &&
            economy.GetBalance(merchantId) == 400,
            label + ": escrow balances mismatch");

        Guid commerceOrderId =
            Guid.NewGuid();

        NexCommerceOrder commerce =
            economy.ExecuteCommerceOrder(
                commerceOrderId,
                NexCommerceKind.MarketplacePurchase,
                payerId,
                merchantId,
                150,
                "MARKETPLACE",
                "ci-marketplace",
                label);

        Require(
            commerce.Status == NexCommerceOrderStatus.Completed &&
            commerce.PaymentTransactionId != Guid.Empty &&
            economy.GetBalance(payerId) == 4235 &&
            economy.GetBalance(merchantId) == 550 &&
            economy.GetBalance(feeId) == 15,
            label + ": commerce settlement mismatch");

        NexCommerceOrder refunded =
            economy.RefundCommerceOrder(
                commerceOrderId,
                "ci-admin",
                "merchant refund");

        Require(
            refunded.Status == NexCommerceOrderStatus.Refunded &&
            refunded.RefundTransactionId != Guid.Empty &&
            economy.GetBalance(payerId) == 4390 &&
            economy.GetBalance(merchantId) == 400 &&
            economy.GetBalance(feeId) == 10,
            label + ": commerce refund mismatch");

        Guid saleListingId =
            Guid.NewGuid();

        NexLandListing sale =
            economy.CreateLandListing(
                new NexLandListing(
                    saleListingId,
                    NexLandListingType.Sale,
                    Guid.NewGuid(),
                    label + " region",
                    Guid.NewGuid(),
                    101,
                    label + " parcel sale",
                    merchantId,
                    Guid.NewGuid(),
                    1024,
                    250));

        Guid landOrderId =
            Guid.NewGuid();

        NexCommerceOrder landPurchase =
            economy.PurchaseLandListing(
                sale.ListingId,
                payerId,
                landOrderId,
                label);

        Require(
            landPurchase.Kind == NexCommerceKind.LandPurchase &&
            landPurchase.Status == NexCommerceOrderStatus.Completed &&
            economy.GetLandListing(sale.ListingId)?.Active == false &&
            economy.GetBalance(payerId) == 4135 &&
            economy.GetBalance(merchantId) == 650 &&
            economy.GetBalance(feeId) == 15,
            label + ": land purchase mismatch");

        Require(
            economy.PurchaseLandListing(
                sale.ListingId,
                payerId,
                landOrderId,
                label).PaymentTransactionId ==
                landPurchase.PaymentTransactionId &&
            economy.GetBalance(payerId) == 4135,
            label + ": land purchase retry was not idempotent");

        NexLandListing rental =
            economy.CreateLandListing(
                new NexLandListing(
                    Guid.NewGuid(),
                    NexLandListingType.Rental,
                    Guid.NewGuid(),
                    label + " rental region",
                    Guid.NewGuid(),
                    202,
                    label + " rental parcel",
                    merchantId,
                    Guid.NewGuid(),
                    512,
                    50,
                    7));

        Guid leaseOrderId =
            Guid.NewGuid();

        NexLandLease lease =
            economy.CreateLandLease(
                rental.ListingId,
                payerId,
                2,
                leaseOrderId,
                label);

        Require(
            lease.Active &&
            lease.TenantAccountId == payerId &&
            lease.LandlordAccountId == merchantId &&
            lease.RentMinor == 50 &&
            economy.GetLandLease(lease.LeaseId) != null &&
            economy.GetBalance(payerId) == 4080 &&
            economy.GetBalance(merchantId) == 700 &&
            economy.GetBalance(feeId) == 20,
            label + ": land lease initial settlement mismatch");

        Require(
            economy.CreateLandLease(
                rental.ListingId,
                payerId,
                2,
                leaseOrderId,
                label).LeaseId ==
                lease.LeaseId &&
            economy.GetBalance(payerId) == 4080,
            label + ": land lease retry was not idempotent");

        Require(
            economy.SearchLandListings(
                label + " rental",
                NexLandListingType.Rental,
                0,
                10).Count == 1,
            label + ": land listing search mismatch");

        NexCommerceOrder ticket =
            economy.ExecuteCommerceOrder(
                Guid.NewGuid(),
                NexCommerceKind.EventTicket,
                payerId,
                merchantId,
                25,
                "EVENT-TICKET",
                "ci-event",
                label);

        Require(
            ticket.Status == NexCommerceOrderStatus.Completed &&
            ticket.Kind == NexCommerceKind.EventTicket,
            label + ": event ticket commerce mismatch");

        Require(
            economy.ListCommerceOrders(
                payerId,
                0,
                20).Count >= 4,
            label + ": commerce purchase history mismatch");

        Require(
            economy.ListLandLeases(
                payerId,
                0,
                10).Count == 1,
            label + ": land lease history mismatch");
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

            RunBankingCommerceRegression(
                store,
                store,
                "sqlite-banking");

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

            using DbCommand virtualAccounts =
                connection.CreateCommand();
            virtualAccounts.CommandText =
                "SELECT COUNT(*) FROM ogl_ledger_virtual_accounts";

            Require(
                Convert.ToInt64(
                    virtualAccounts.ExecuteScalar()) >= 2,
                "sqlite: NVBAN mappings were not persisted");

            string[] workflowTables =
            {
                "ogl_economy_transfer_policies",
                "ogl_economy_payment_requests",
                "ogl_economy_commerce_orders",
                "ogl_economy_land_listings",
                "ogl_economy_land_leases"
            };

            foreach (string table in workflowTables)
            {
                using DbCommand count =
                    connection.CreateCommand();
                count.CommandText =
                    "SELECT COUNT(*) FROM " +
                    table;

                Require(
                    Convert.ToInt64(
                        count.ExecuteScalar()) > 0,
                    "sqlite: workflow rows missing from " +
                    table);
            }

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

        RunBankingCommerceRegression(
            memory,
            memory,
            "memory-banking");

        RunSqlitePersistence();

        Console.WriteLine(
            "OpenGenesisLINK NV$ banking, commerce, land and policy regression: OK");

        return 0;
    }
}
