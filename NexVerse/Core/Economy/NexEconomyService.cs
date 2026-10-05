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
        private readonly INexVirtualBankAccountStore m_VirtualAccounts;
        private readonly INexEconomyWorkflowStore m_Workflows;

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
            m_VirtualAccounts =
                states as INexVirtualBankAccountStore;
            m_Workflows =
                states as INexEconomyWorkflowStore;
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

        public NexVirtualBankAccount EnsureVirtualBankAccount(Guid accountId)
        {
            NexLedgerAccount account =
                RequireWalletAccount(accountId, "virtual bank");

            if (m_VirtualAccounts == null)
                throw new InvalidOperationException(
                    "Virtual NVBAN account storage is not available.");

            NexVirtualBankAccount existing =
                m_VirtualAccounts.GetVirtualBankAccount(account.AccountId);

            if (existing != null)
                return existing;

            return m_VirtualAccounts.GetOrCreateVirtualBankAccount(
                account.AccountId,
                NexVirtualBankAccount.CreateIdentifier(account.AccountId),
                DateTimeOffset.UtcNow);
        }

        public NexVirtualBankAccount GetVirtualBankAccount(Guid accountId)
        {
            if (m_VirtualAccounts == null)
                return null;

            return m_VirtualAccounts.GetVirtualBankAccount(accountId);
        }

        public NexVirtualBankAccount ResolveVirtualBankAccount(string identifier)
        {
            if (m_VirtualAccounts == null)
                throw new InvalidOperationException(
                    "Virtual NVBAN account storage is not available.");

            return m_VirtualAccounts.GetVirtualBankAccountByIdentifier(identifier);
        }


        public NexLedgerAccount EnsureWalletAccount(
            Guid accountId,
            NexLedgerAccountClass accountClass,
            string displayName)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException("Wallet account ID is required.", nameof(accountId));
            if (!IsWalletClass(accountClass))
                throw new NexLedgerPolicyException("Requested account class is not a transferable NV$ wallet.");

            NexLedgerAccount existing =
                m_Ledger.GetAccount(accountId);

            if (existing != null)
            {
                if (existing.AccountClass != accountClass ||
                    existing.NormalSide != NexLedgerSide.Credit)
                {
                    throw new NexLedgerConflictException(
                        "Account UUID is already used by an incompatible ledger account.");
                }

                return existing;
            }

            string prefix =
                accountClass
                    .ToString()
                    .ToLowerInvariant();

            NexLedgerAccount created =
                new NexLedgerAccount(
                    accountId,
                    accountClass,
                    NexLedgerSide.Credit,
                    prefix + ":" + accountId.ToString("D"),
                    string.IsNullOrWhiteSpace(displayName)
                        ? accountId.ToString("D")
                        : displayName);

            if (m_Ledger.TryCreateAccount(created))
                return created;

            return RequireAccount(accountId);
        }

        public IReadOnlyList<NexLedgerTransaction> ListTransactions(
            Guid accountId,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            int offset = 0,
            int limit = 100)
        {
            RequireAccount(accountId);

            if (from.HasValue &&
                to.HasValue &&
                to.Value < from.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(to));
            }

            return m_Ledger.ListTransactions(
                accountId,
                from,
                to,
                offset,
                limit);
        }

        public NexBankStatement GetStatement(
            Guid accountId,
            DateTimeOffset from,
            DateTimeOffset to,
            int offset = 0,
            int limit = 500)
        {
            RequireAccount(accountId);

            if (to < from)
                throw new ArgumentOutOfRangeException(nameof(to));

            DateTimeOffset beforeFrom =
                from == DateTimeOffset.MinValue
                    ? from
                    : from.AddTicks(-1);

            long opening =
                m_Ledger.GetBalanceAt(
                    accountId,
                    beforeFrom);

            long closing =
                m_Ledger.GetBalanceAt(
                    accountId,
                    to);

            return new NexBankStatement(
                accountId,
                from,
                to,
                opening,
                closing,
                m_Ledger.ListTransactions(
                    accountId,
                    from,
                    to,
                    offset,
                    limit));
        }

        public NexReconciliationReport ReconcileAccount(
            Guid accountId)
        {
            RequireAccount(accountId);

            long derived =
                m_Ledger.GetBalance(accountId);
            long recomputed =
                m_Ledger.GetBalanceAt(
                    accountId,
                    DateTimeOffset.MaxValue);

            IReadOnlyList<NexLedgerTransaction> transactions =
                m_Ledger.ListTransactions(
                    accountId,
                    null,
                    null,
                    0,
                    1000);

            bool balanced =
                derived == recomputed;

            foreach (NexLedgerTransaction transaction in transactions)
                transaction.Validate();

            return new NexReconciliationReport(
                accountId,
                derived,
                recomputed,
                transactions.Count,
                balanced);
        }

        public NexAccountTransferPolicy GetTransferPolicy(
            Guid accountId)
        {
            RequireWorkflows();
            RequireAccount(accountId);

            return
                m_Workflows.GetTransferPolicy(accountId) ??
                new NexAccountTransferPolicy(accountId);
        }

        public NexAccountTransferPolicy SetTransferPolicy(
            NexAccountTransferPolicy policy)
        {
            RequireWorkflows();

            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            RequireWalletAccount(
                policy.AccountId,
                "policy");

            if (policy.FlatFeeMinor > 0)
            {
                NexLedgerAccount feeAccount =
                    RequireWalletAccount(
                        policy.FeeAccountId,
                        "fee");

                RequireActive(
                    feeAccount.AccountId,
                    "fee");
            }

            return m_Workflows.SetTransferPolicy(policy);
        }

        public NexLedgerAppendResult BankingTransfer(
            Guid fromAccountId,
            Guid toAccountId,
            long amountMinor,
            string reference,
            string correlationId = null,
            Guid? transactionId = null)
        {
            RequireWorkflows();

            if (fromAccountId == toAccountId)
                throw new NexLedgerPolicyException("Source and destination accounts must differ.");
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));

            NexLedgerAccount from =
                RequireWalletAccount(fromAccountId, "source");
            NexLedgerAccount to =
                RequireWalletAccount(toAccountId, "destination");

            RequireActive(from.AccountId, "source");
            RequireActive(to.AccountId, "destination");

            NexAccountTransferPolicy policy =
                GetTransferPolicy(fromAccountId);

            if (policy.MaxPerTransferMinor > 0 &&
                amountMinor > policy.MaxPerTransferMinor)
            {
                throw new NexLedgerPolicyException(
                    "Transfer exceeds the configured per-transaction NV$ limit.");
            }

            long fee =
                policy.FlatFeeMinor;

            long totalDebit =
                checked(amountMinor + fee);

            if (policy.DailyOutgoingLimitMinor > 0)
            {
                DateTimeOffset now =
                    DateTimeOffset.UtcNow;
                DateTimeOffset dayStart =
                    new DateTimeOffset(
                        now.Year,
                        now.Month,
                        now.Day,
                        0,
                        0,
                        0,
                        TimeSpan.Zero);

                long used =
                    m_Workflows.GetOutgoingTotal(
                        fromAccountId,
                        dayStart,
                        dayStart.AddDays(1));

                if (checked(used + totalDebit) >
                    policy.DailyOutgoingLimitMinor)
                {
                    throw new NexLedgerPolicyException(
                        "Transfer exceeds the configured daily NV$ outgoing limit.");
                }
            }

            RequireAvailableBalance(
                from,
                totalDebit);

            string normalizedReference =
                RequireReference(reference);
            Guid effectiveId =
                transactionId ??
                Guid.NewGuid();

            NexLedgerTransaction existing =
                m_Ledger.GetTransaction(effectiveId);

            if (existing != null)
            {
                if (!MatchesBankingTransfer(
                        existing,
                        fromAccountId,
                        toAccountId,
                        amountMinor,
                        fee,
                        policy.FeeAccountId,
                        normalizedReference))
                {
                    throw new NexLedgerConflictException(
                        "Transaction ID already exists with different banking-transfer content.");
                }

                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Duplicate,
                    existing);
            }

            List<NexLedgerPosting> postings =
                new List<NexLedgerPosting>
                {
                    new NexLedgerPosting(
                        DeterministicGuid("bank-transfer-debit:" + effectiveId.ToString("D")),
                        from.AccountId,
                        NexLedgerSide.Debit,
                        totalDebit,
                        "NV$ banking transfer debit"),
                    new NexLedgerPosting(
                        DeterministicGuid("bank-transfer-credit:" + effectiveId.ToString("D")),
                        to.AccountId,
                        NexLedgerSide.Credit,
                        amountMinor,
                        "NV$ banking transfer credit")
                };

            if (fee > 0)
            {
                NexLedgerAccount feeAccount =
                    RequireWalletAccount(
                        policy.FeeAccountId,
                        "fee");
                RequireActive(feeAccount.AccountId, "fee");

                postings.Add(
                    new NexLedgerPosting(
                        DeterministicGuid("bank-transfer-fee:" + effectiveId.ToString("D")),
                        feeAccount.AccountId,
                        NexLedgerSide.Credit,
                        fee,
                        "NV$ transfer fee"));
            }

            Dictionary<string, string> metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["from_account_id"] = fromAccountId.ToString("D"),
                    ["to_account_id"] = toAccountId.ToString("D"),
                    ["amount_minor"] = amountMinor.ToString(),
                    ["fee_minor"] = fee.ToString(),
                    ["fee_account_id"] = policy.FeeAccountId.ToString("D")
                };

            return m_Ledger.Post(
                new NexLedgerTransaction(
                    effectiveId,
                    "bank_transfer",
                    normalizedReference,
                    postings,
                    correlationId,
                    metadata));
        }

        public NexPaymentRequest CreatePaymentRequest(
            Guid payeeAccountId,
            Guid payerAccountId,
            long amountMinor,
            string reference,
            DateTimeOffset? expiresAt = null,
            Guid? requestId = null)
        {
            RequireWorkflows();

            NexLedgerAccount payee =
                RequireWalletAccount(payeeAccountId, "payee");
            NexLedgerAccount payer =
                RequireWalletAccount(payerAccountId, "payer");

            RequireActive(payee.AccountId, "payee");
            RequireActive(payer.AccountId, "payer");

            NexPaymentRequest request =
                new NexPaymentRequest(
                    requestId ?? Guid.NewGuid(),
                    payeeAccountId,
                    payerAccountId,
                    amountMinor,
                    reference,
                    NexPaymentRequestStatus.Pending,
                    DateTimeOffset.UtcNow,
                    expiresAt);

            return m_Workflows.CreatePaymentRequest(request);
        }

        public NexPaymentRequest GetPaymentRequest(Guid requestId)
        {
            RequireWorkflows();

            NexPaymentRequest request =
                m_Workflows.GetPaymentRequest(requestId);

            if (request != null &&
                request.Status == NexPaymentRequestStatus.Pending &&
                request.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                request =
                    m_Workflows.UpdatePaymentRequest(
                        request.WithStatus(
                            NexPaymentRequestStatus.Expired));
            }

            return request;
        }

        public IReadOnlyList<NexPaymentRequest> ListPaymentRequests(
            Guid accountId,
            int offset = 0,
            int limit = 100)
        {
            RequireWorkflows();
            RequireAccount(accountId);

            return m_Workflows.ListPaymentRequests(
                accountId,
                offset,
                limit);
        }

        public NexPaymentRequest PayPaymentRequest(
            Guid requestId,
            Guid payerAccountId,
            string correlationId = null)
        {
            RequireWorkflows();

            NexPaymentRequest request =
                GetPaymentRequest(requestId) ??
                throw new NexLedgerPolicyException("Payment request was not found.");

            if (request.Status == NexPaymentRequestStatus.Paid)
                return request;

            if (request.Status != NexPaymentRequestStatus.Pending)
                throw new NexLedgerPolicyException("Payment request is not payable.");

            if (request.PayerAccountId != payerAccountId)
                throw new NexLedgerPolicyException("Payment request payer does not match.");

            Guid transactionId =
                DeterministicGuid(
                    "payment-request:" +
                    request.RequestId.ToString("D"));

            NexLedgerAppendResult result =
                BankingTransfer(
                    request.PayerAccountId,
                    request.PayeeAccountId,
                    request.AmountMinor,
                    request.Reference,
                    correlationId,
                    transactionId);

            return m_Workflows.UpdatePaymentRequest(
                request.WithStatus(
                    NexPaymentRequestStatus.Paid,
                    result.Transaction.TransactionId));
        }

        public NexPaymentRequest CancelPaymentRequest(
            Guid requestId,
            Guid payeeAccountId)
        {
            RequireWorkflows();

            NexPaymentRequest request =
                GetPaymentRequest(requestId) ??
                throw new NexLedgerPolicyException("Payment request was not found.");

            if (request.PayeeAccountId != payeeAccountId)
                throw new NexLedgerPolicyException("Only the payee can cancel the payment request.");

            if (request.Status != NexPaymentRequestStatus.Pending)
                return request;

            return m_Workflows.UpdatePaymentRequest(
                request.WithStatus(
                    NexPaymentRequestStatus.Cancelled));
        }

        public NexLedgerAppendResult Refund(
            Guid originalTransactionId,
            string actor,
            string reason) =>
            Reverse(
                originalTransactionId,
                actor,
                reason);

        public NexCommerceOrder ExecuteCommerceOrder(
            Guid orderId,
            NexCommerceKind kind,
            Guid buyerAccountId,
            Guid sellerAccountId,
            long amountMinor,
            string reference,
            string externalReference = "",
            string correlationId = null)
        {
            RequireWorkflows();

            NexCommerceOrder existing =
                m_Workflows.GetCommerceOrder(orderId);

            if (existing != null)
            {
                if (existing.Kind != kind ||
                    existing.BuyerAccountId != buyerAccountId ||
                    existing.SellerAccountId != sellerAccountId ||
                    existing.AmountMinor != amountMinor ||
                    !string.Equals(existing.Reference, reference?.Trim(), StringComparison.Ordinal) ||
                    !string.Equals(existing.ExternalReference, (externalReference ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    throw new NexLedgerConflictException(
                        "Commerce order ID already exists with different immutable content.");
                }

                if (existing.Status == NexCommerceOrderStatus.Completed ||
                    existing.Status == NexCommerceOrderStatus.Refunded)
                {
                    return existing;
                }
            }
            else
            {
                existing =
                    m_Workflows.CreateCommerceOrder(
                        new NexCommerceOrder(
                            orderId,
                            kind,
                            buyerAccountId,
                            sellerAccountId,
                            amountMinor,
                            reference,
                            externalReference));
            }

            Guid transactionId =
                DeterministicGuid(
                    "commerce-order:" +
                    orderId.ToString("D"));

            NexLedgerAppendResult payment =
                BankingTransfer(
                    buyerAccountId,
                    sellerAccountId,
                    amountMinor,
                    reference,
                    correlationId,
                    transactionId);

            return m_Workflows.UpdateCommerceOrder(
                existing.Complete(
                    payment.Transaction.TransactionId));
        }

        public NexCommerceOrder GetCommerceOrder(Guid orderId)
        {
            RequireWorkflows();
            return m_Workflows.GetCommerceOrder(orderId);
        }

        public IReadOnlyList<NexCommerceOrder> ListCommerceOrders(
            Guid accountId,
            int offset = 0,
            int limit = 100)
        {
            RequireWorkflows();
            RequireAccount(accountId);

            return m_Workflows.ListCommerceOrders(
                accountId,
                offset,
                limit);
        }

        public NexCommerceOrder RefundCommerceOrder(
            Guid orderId,
            string actor,
            string reason)
        {
            RequireWorkflows();

            NexCommerceOrder order =
                m_Workflows.GetCommerceOrder(orderId) ??
                throw new NexLedgerPolicyException("Commerce order was not found.");

            if (order.Status == NexCommerceOrderStatus.Refunded)
                return order;

            if (order.Status != NexCommerceOrderStatus.Completed ||
                order.PaymentTransactionId == Guid.Empty)
            {
                throw new NexLedgerPolicyException(
                    "Only completed commerce orders can be refunded.");
            }

            NexLedgerAppendResult refund =
                Refund(
                    order.PaymentTransactionId,
                    actor,
                    reason);

            return m_Workflows.UpdateCommerceOrder(
                order.Refund(
                    refund.Transaction.TransactionId));
        }

        public NexLandListing CreateLandListing(
            NexLandListing listing)
        {
            RequireWorkflows();

            if (listing == null)
                throw new ArgumentNullException(nameof(listing));

            RequireWalletAccount(
                listing.SellerAccountId,
                "land seller");

            return m_Workflows.CreateLandListing(listing);
        }

        public NexLandListing GetLandListing(Guid listingId)
        {
            RequireWorkflows();
            return m_Workflows.GetLandListing(listingId);
        }

        public IReadOnlyList<NexLandListing> SearchLandListings(
            string query,
            NexLandListingType? type,
            int offset = 0,
            int limit = 100)
        {
            RequireWorkflows();

            return m_Workflows.SearchLandListings(
                query,
                type,
                offset,
                limit);
        }

        public NexLandListing DeactivateLandListing(
            Guid listingId,
            Guid sellerAccountId)
        {
            RequireWorkflows();

            NexLandListing listing =
                m_Workflows.GetLandListing(listingId) ??
                throw new NexLedgerPolicyException("Land listing was not found.");

            if (listing.SellerAccountId != sellerAccountId)
                throw new NexLedgerPolicyException("Only the seller can deactivate the land listing.");

            if (!listing.Active)
                return listing;

            return m_Workflows.UpdateLandListing(
                listing.Deactivate());
        }

        public NexCommerceOrder PurchaseLandListing(
            Guid listingId,
            Guid buyerAccountId,
            Guid orderId,
            string correlationId = null)
        {
            RequireWorkflows();

            NexLandListing listing =
                m_Workflows.GetLandListing(listingId) ??
                throw new NexLedgerPolicyException("Land listing was not found.");

            if (!listing.Active ||
                listing.ListingType != NexLandListingType.Sale)
            {
                throw new NexLedgerPolicyException(
                    "Land listing is not an active sale listing.");
            }

            NexCommerceOrder order =
                ExecuteCommerceOrder(
                    orderId,
                    NexCommerceKind.LandPurchase,
                    buyerAccountId,
                    listing.SellerAccountId,
                    listing.PriceMinor,
                    "Land purchase: " + listing.ParcelName,
                    "land-listing:" + listing.ListingId.ToString("D"),
                    correlationId);

            m_Workflows.UpdateLandListing(
                listing.Deactivate());

            return order;
        }

        public NexLandLease CreateLandLease(
            Guid listingId,
            Guid tenantAccountId,
            int periods,
            Guid orderId,
            string correlationId = null)
        {
            RequireWorkflows();

            if (periods < 1 || periods > 120)
                throw new ArgumentOutOfRangeException(nameof(periods));

            NexLandListing listing =
                m_Workflows.GetLandListing(listingId) ??
                throw new NexLedgerPolicyException("Land listing was not found.");

            if (!listing.Active ||
                listing.ListingType != NexLandListingType.Rental)
            {
                throw new NexLedgerPolicyException(
                    "Land listing is not an active rental listing.");
            }

            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            ExecuteCommerceOrder(
                orderId,
                NexCommerceKind.Rental,
                tenantAccountId,
                listing.SellerAccountId,
                listing.PriceMinor,
                "Land rent: " + listing.ParcelName,
                "land-rental:" + listing.ListingId.ToString("D"),
                correlationId);

            Guid leaseId =
                DeterministicGuid(
                    "land-lease:" +
                    listingId.ToString("D") +
                    ":" +
                    tenantAccountId.ToString("D"));

            NexLandLease existing =
                m_Workflows.GetLandLease(leaseId);

            if (existing != null)
                return existing;

            return m_Workflows.CreateLandLease(
                new NexLandLease(
                    leaseId,
                    listingId,
                    tenantAccountId,
                    listing.SellerAccountId,
                    listing.PriceMinor,
                    listing.RentalPeriodDays,
                    now,
                    now.AddDays(
                        checked(listing.RentalPeriodDays * periods)),
                    now.AddDays(listing.RentalPeriodDays),
                    true,
                    DeterministicGuid(
                        "commerce-order:" +
                        orderId.ToString("D"))));
        }

        public NexLandLease PayLandRent(
            Guid leaseId,
            Guid orderId,
            string correlationId = null)
        {
            RequireWorkflows();

            NexLandLease lease =
                m_Workflows.GetLandLease(leaseId) ??
                throw new NexLedgerPolicyException("Land lease was not found.");

            if (!lease.Active)
                throw new NexLedgerPolicyException("Land lease is not active.");

            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            if (now >= lease.EndsAt ||
                lease.NextDueAt >= lease.EndsAt)
            {
                throw new NexLedgerPolicyException(
                    "No further rent payment is due for this lease.");
            }

            if (now < lease.NextDueAt)
                throw new NexLedgerPolicyException("Land rent is not due yet.");

            NexCommerceOrder payment =
                ExecuteCommerceOrder(
                    orderId,
                    NexCommerceKind.Rental,
                    lease.TenantAccountId,
                    lease.LandlordAccountId,
                    lease.RentMinor,
                    "Recurring land rent",
                    "land-lease:" + lease.LeaseId.ToString("D"),
                    correlationId);

            DateTimeOffset next =
                lease.NextDueAt.AddDays(
                    lease.PeriodDays);

            bool active =
                now < lease.EndsAt;

            return m_Workflows.UpdateLandLease(
                lease.Advance(
                    payment.PaymentTransactionId,
                    next,
                    active));
        }

        public NexLandLease GetLandLease(
            Guid leaseId)
        {
            RequireWorkflows();
            return m_Workflows.GetLandLease(leaseId);
        }

        public IReadOnlyList<NexLandLease> ListLandLeases(
            Guid accountId,
            int offset = 0,
            int limit = 100)
        {
            RequireWorkflows();
            RequireAccount(accountId);

            return m_Workflows.ListLandLeases(
                accountId,
                offset,
                limit);
        }

        public NexLedgerAccount EnsureEscrowAccount(
            Guid escrowId,
            string displayName)
        {
            if (escrowId == Guid.Empty)
                throw new ArgumentException("Escrow account ID is required.", nameof(escrowId));

            NexLedgerAccount existing =
                m_Ledger.GetAccount(escrowId);

            if (existing != null)
            {
                if (existing.AccountClass != NexLedgerAccountClass.Escrow ||
                    existing.NormalSide != NexLedgerSide.Credit)
                {
                    throw new NexLedgerConflictException(
                        "Escrow UUID is already used by an incompatible account.");
                }

                return existing;
            }

            NexLedgerAccount created =
                new NexLedgerAccount(
                    escrowId,
                    NexLedgerAccountClass.Escrow,
                    NexLedgerSide.Credit,
                    "escrow:" + escrowId.ToString("D"),
                    string.IsNullOrWhiteSpace(displayName)
                        ? "NV$ Escrow"
                        : displayName);

            if (m_Ledger.TryCreateAccount(created))
                return created;

            return RequireAccount(escrowId);
        }

        public NexLedgerAppendResult FundEscrow(
            Guid fromAccountId,
            Guid escrowId,
            long amountMinor,
            string reference,
            string correlationId = null,
            Guid? transactionId = null)
        {
            NexLedgerAccount from =
                RequireWalletAccount(fromAccountId, "escrow source");
            NexLedgerAccount escrow =
                EnsureEscrowAccount(
                    escrowId,
                    "NV$ Escrow");

            RequireActive(from.AccountId, "escrow source");
            RequireActive(escrow.AccountId, "escrow");
            RequireAvailableBalance(from, amountMinor);

            return PostDirectedTransfer(
                "escrow_fund",
                from.AccountId,
                escrow.AccountId,
                amountMinor,
                reference,
                correlationId,
                transactionId ?? Guid.NewGuid());
        }

        public NexLedgerAppendResult ReleaseEscrow(
            Guid escrowId,
            Guid toAccountId,
            long amountMinor,
            string reference,
            string correlationId = null,
            Guid? transactionId = null)
        {
            NexLedgerAccount escrow =
                EnsureEscrowAccount(
                    escrowId,
                    "NV$ Escrow");
            NexLedgerAccount to =
                RequireWalletAccount(toAccountId, "escrow destination");

            RequireActive(escrow.AccountId, "escrow");
            RequireActive(to.AccountId, "escrow destination");
            RequireAvailableBalance(escrow, amountMinor);

            return PostDirectedTransfer(
                "escrow_release",
                escrow.AccountId,
                to.AccountId,
                amountMinor,
                reference,
                correlationId,
                transactionId ?? Guid.NewGuid());
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


        private void RequireWorkflows()
        {
            if (m_Workflows == null)
            {
                throw new InvalidOperationException(
                    "Banking/commerce workflow storage is not available.");
            }
        }

        private NexLedgerAppendResult PostDirectedTransfer(
            string kind,
            Guid fromAccountId,
            Guid toAccountId,
            long amountMinor,
            string reference,
            string correlationId,
            Guid transactionId)
        {
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));

            string normalizedReference =
                RequireReference(reference);

            NexLedgerTransaction existing =
                m_Ledger.GetTransaction(transactionId);

            if (existing != null)
                return new NexLedgerAppendResult(
                    NexLedgerAppendStatus.Duplicate,
                    existing);

            return m_Ledger.Post(
                new NexLedgerTransaction(
                    transactionId,
                    kind,
                    normalizedReference,
                    new[]
                    {
                        new NexLedgerPosting(
                            DeterministicGuid(kind + ":debit:" + transactionId.ToString("D")),
                            fromAccountId,
                            NexLedgerSide.Debit,
                            amountMinor,
                            kind + " debit"),
                        new NexLedgerPosting(
                            DeterministicGuid(kind + ":credit:" + transactionId.ToString("D")),
                            toAccountId,
                            NexLedgerSide.Credit,
                            amountMinor,
                            kind + " credit")
                    },
                    correlationId));
        }

        private static bool MatchesBankingTransfer(
            NexLedgerTransaction transaction,
            Guid fromAccountId,
            Guid toAccountId,
            long amountMinor,
            long feeMinor,
            Guid feeAccountId,
            string reference)
        {
            if (transaction == null ||
                !string.Equals(transaction.Kind, "bank_transfer", StringComparison.Ordinal) ||
                !string.Equals(transaction.Reference, reference, StringComparison.Ordinal))
            {
                return false;
            }

            if (!transaction.Metadata.TryGetValue("from_account_id", out string from) ||
                !transaction.Metadata.TryGetValue("to_account_id", out string to) ||
                !transaction.Metadata.TryGetValue("amount_minor", out string amount) ||
                !transaction.Metadata.TryGetValue("fee_minor", out string fee) ||
                !transaction.Metadata.TryGetValue("fee_account_id", out string feeAccount))
            {
                return false;
            }

            return
                string.Equals(from, fromAccountId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(to, toAccountId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(amount, amountMinor.ToString(), StringComparison.Ordinal) &&
                string.Equals(fee, feeMinor.ToString(), StringComparison.Ordinal) &&
                string.Equals(feeAccount, feeAccountId.ToString("D"), StringComparison.OrdinalIgnoreCase);
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
