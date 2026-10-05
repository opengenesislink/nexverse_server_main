// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Economy
{
    public enum NexPaymentRequestStatus
    {
        Pending = 1,
        Paid = 2,
        Cancelled = 3,
        Expired = 4
    }

    public sealed class NexPaymentRequest
    {
        public const int ReferenceLengthLimit = 255;

        public NexPaymentRequest(
            Guid requestId,
            Guid payeeAccountId,
            Guid payerAccountId,
            long amountMinor,
            string reference,
            NexPaymentRequestStatus status = NexPaymentRequestStatus.Pending,
            DateTimeOffset? createdAt = null,
            DateTimeOffset? expiresAt = null,
            Guid? paymentTransactionId = null)
        {
            if (requestId == Guid.Empty)
                throw new ArgumentException("Payment request ID is required.", nameof(requestId));
            if (payeeAccountId == Guid.Empty)
                throw new ArgumentException("Payee account ID is required.", nameof(payeeAccountId));
            if (payerAccountId == Guid.Empty)
                throw new ArgumentException("Payer account ID is required.", nameof(payerAccountId));
            if (payeeAccountId == payerAccountId)
                throw new ArgumentException("Payer and payee must differ.");
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));
            if (!Enum.IsDefined(typeof(NexPaymentRequestStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));

            string normalized =
                (reference ?? string.Empty).Trim();
            if (normalized.Length == 0)
                throw new ArgumentException("Payment request reference is required.", nameof(reference));
            if (normalized.Length > ReferenceLengthLimit)
                throw new ArgumentOutOfRangeException(nameof(reference));

            DateTimeOffset created =
                createdAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset expires =
                expiresAt ?? created.AddDays(30);
            if (expires <= created)
                throw new ArgumentOutOfRangeException(nameof(expiresAt));

            RequestId = requestId;
            PayeeAccountId = payeeAccountId;
            PayerAccountId = payerAccountId;
            AmountMinor = amountMinor;
            Reference = normalized;
            Status = status;
            CreatedAt = created;
            ExpiresAt = expires;
            PaymentTransactionId = paymentTransactionId ?? Guid.Empty;
        }

        public Guid RequestId { get; }
        public Guid PayeeAccountId { get; }
        public Guid PayerAccountId { get; }
        public long AmountMinor { get; }
        public string Reference { get; }
        public NexPaymentRequestStatus Status { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public Guid PaymentTransactionId { get; }

        public NexPaymentRequest WithStatus(
            NexPaymentRequestStatus status,
            Guid paymentTransactionId = default) =>
            new NexPaymentRequest(
                RequestId,
                PayeeAccountId,
                PayerAccountId,
                AmountMinor,
                Reference,
                status,
                CreatedAt,
                ExpiresAt,
                paymentTransactionId);
    }

    public sealed class NexAccountTransferPolicy
    {
        public NexAccountTransferPolicy(
            Guid accountId,
            long maxPerTransferMinor = 0,
            long dailyOutgoingLimitMinor = 0,
            long flatFeeMinor = 0,
            Guid feeAccountId = default)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException("Account ID is required.", nameof(accountId));
            if (maxPerTransferMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(maxPerTransferMinor));
            if (dailyOutgoingLimitMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(dailyOutgoingLimitMinor));
            if (flatFeeMinor < 0)
                throw new ArgumentOutOfRangeException(nameof(flatFeeMinor));
            if (flatFeeMinor > 0 && feeAccountId == Guid.Empty)
                throw new ArgumentException("Fee account is required when a flat fee is configured.", nameof(feeAccountId));

            AccountId = accountId;
            MaxPerTransferMinor = maxPerTransferMinor;
            DailyOutgoingLimitMinor = dailyOutgoingLimitMinor;
            FlatFeeMinor = flatFeeMinor;
            FeeAccountId = feeAccountId;
        }

        public Guid AccountId { get; }
        public long MaxPerTransferMinor { get; }
        public long DailyOutgoingLimitMinor { get; }
        public long FlatFeeMinor { get; }
        public Guid FeeAccountId { get; }
    }

    public enum NexCommerceKind
    {
        VendorPayment = 1,
        ObjectSale = 2,
        MarketplacePurchase = 3,
        LandPurchase = 4,
        EventTicket = 5,
        Rental = 6,
        MerchantRefund = 7
    }

    public enum NexCommerceOrderStatus
    {
        Pending = 1,
        Completed = 2,
        Refunded = 3,
        Cancelled = 4
    }

    public sealed class NexCommerceOrder
    {
        public const int ReferenceLengthLimit = 255;
        public const int ExternalReferenceLengthLimit = 255;

        public NexCommerceOrder(
            Guid orderId,
            NexCommerceKind kind,
            Guid buyerAccountId,
            Guid sellerAccountId,
            long amountMinor,
            string reference,
            string externalReference = "",
            NexCommerceOrderStatus status = NexCommerceOrderStatus.Pending,
            Guid paymentTransactionId = default,
            Guid refundTransactionId = default,
            DateTimeOffset? createdAt = null,
            DateTimeOffset? completedAt = null)
        {
            if (orderId == Guid.Empty)
                throw new ArgumentException("Commerce order ID is required.", nameof(orderId));
            if (!Enum.IsDefined(typeof(NexCommerceKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (!Enum.IsDefined(typeof(NexCommerceOrderStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (buyerAccountId == Guid.Empty)
                throw new ArgumentException("Buyer account ID is required.", nameof(buyerAccountId));
            if (sellerAccountId == Guid.Empty)
                throw new ArgumentException("Seller account ID is required.", nameof(sellerAccountId));
            if (buyerAccountId == sellerAccountId)
                throw new ArgumentException("Buyer and seller must differ.");
            if (amountMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountMinor));

            string normalizedReference =
                (reference ?? string.Empty).Trim();
            string normalizedExternal =
                (externalReference ?? string.Empty).Trim();
            if (normalizedReference.Length == 0)
                throw new ArgumentException("Commerce reference is required.", nameof(reference));
            if (normalizedReference.Length > ReferenceLengthLimit)
                throw new ArgumentOutOfRangeException(nameof(reference));
            if (normalizedExternal.Length > ExternalReferenceLengthLimit)
                throw new ArgumentOutOfRangeException(nameof(externalReference));

            OrderId = orderId;
            Kind = kind;
            BuyerAccountId = buyerAccountId;
            SellerAccountId = sellerAccountId;
            AmountMinor = amountMinor;
            Reference = normalizedReference;
            ExternalReference = normalizedExternal;
            Status = status;
            PaymentTransactionId = paymentTransactionId;
            RefundTransactionId = refundTransactionId;
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
            CompletedAt = completedAt;
        }

        public Guid OrderId { get; }
        public NexCommerceKind Kind { get; }
        public Guid BuyerAccountId { get; }
        public Guid SellerAccountId { get; }
        public long AmountMinor { get; }
        public string Reference { get; }
        public string ExternalReference { get; }
        public NexCommerceOrderStatus Status { get; }
        public Guid PaymentTransactionId { get; }
        public Guid RefundTransactionId { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? CompletedAt { get; }

        public NexCommerceOrder Complete(Guid transactionId) =>
            new NexCommerceOrder(
                OrderId,
                Kind,
                BuyerAccountId,
                SellerAccountId,
                AmountMinor,
                Reference,
                ExternalReference,
                NexCommerceOrderStatus.Completed,
                transactionId,
                RefundTransactionId,
                CreatedAt,
                DateTimeOffset.UtcNow);

        public NexCommerceOrder Refund(Guid transactionId) =>
            new NexCommerceOrder(
                OrderId,
                Kind,
                BuyerAccountId,
                SellerAccountId,
                AmountMinor,
                Reference,
                ExternalReference,
                NexCommerceOrderStatus.Refunded,
                PaymentTransactionId,
                transactionId,
                CreatedAt,
                CompletedAt);
    }

    public enum NexLandListingType
    {
        Sale = 1,
        Rental = 2
    }

    public sealed class NexLandListing
    {
        public NexLandListing(
            Guid listingId,
            NexLandListingType listingType,
            Guid regionId,
            string regionName,
            Guid parcelId,
            int parcelLocalId,
            string parcelName,
            Guid sellerAccountId,
            Guid estateId,
            int area,
            long priceMinor,
            int rentalPeriodDays = 0,
            bool active = true,
            DateTimeOffset? createdAt = null)
        {
            if (listingId == Guid.Empty)
                throw new ArgumentException("Listing ID is required.", nameof(listingId));
            if (!Enum.IsDefined(typeof(NexLandListingType), listingType))
                throw new ArgumentOutOfRangeException(nameof(listingType));
            if (regionId == Guid.Empty)
                throw new ArgumentException("Region ID is required.", nameof(regionId));
            if (parcelId == Guid.Empty)
                throw new ArgumentException("Parcel ID is required.", nameof(parcelId));
            if (parcelLocalId <= 0)
                throw new ArgumentOutOfRangeException(nameof(parcelLocalId));
            if (sellerAccountId == Guid.Empty)
                throw new ArgumentException("Seller account ID is required.", nameof(sellerAccountId));
            if (area <= 0)
                throw new ArgumentOutOfRangeException(nameof(area));
            if (priceMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(priceMinor));
            if (listingType == NexLandListingType.Rental && rentalPeriodDays <= 0)
                throw new ArgumentOutOfRangeException(nameof(rentalPeriodDays));

            ListingId = listingId;
            ListingType = listingType;
            RegionId = regionId;
            RegionName = (regionName ?? string.Empty).Trim();
            ParcelId = parcelId;
            ParcelLocalId = parcelLocalId;
            ParcelName = (parcelName ?? string.Empty).Trim();
            SellerAccountId = sellerAccountId;
            EstateId = estateId;
            Area = area;
            PriceMinor = priceMinor;
            RentalPeriodDays = rentalPeriodDays;
            Active = active;
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        }

        public Guid ListingId { get; }
        public NexLandListingType ListingType { get; }
        public Guid RegionId { get; }
        public string RegionName { get; }
        public Guid ParcelId { get; }
        public int ParcelLocalId { get; }
        public string ParcelName { get; }
        public Guid SellerAccountId { get; }
        public Guid EstateId { get; }
        public int Area { get; }
        public long PriceMinor { get; }
        public int RentalPeriodDays { get; }
        public bool Active { get; }
        public DateTimeOffset CreatedAt { get; }

        public NexLandListing Deactivate() =>
            new NexLandListing(
                ListingId,
                ListingType,
                RegionId,
                RegionName,
                ParcelId,
                ParcelLocalId,
                ParcelName,
                SellerAccountId,
                EstateId,
                Area,
                PriceMinor,
                RentalPeriodDays,
                false,
                CreatedAt);
    }

    public sealed class NexLandLease
    {
        public NexLandLease(
            Guid leaseId,
            Guid listingId,
            Guid tenantAccountId,
            Guid landlordAccountId,
            long rentMinor,
            int periodDays,
            DateTimeOffset startsAt,
            DateTimeOffset endsAt,
            DateTimeOffset nextDueAt,
            bool active = true,
            Guid lastPaymentTransactionId = default)
        {
            if (leaseId == Guid.Empty)
                throw new ArgumentException("Lease ID is required.", nameof(leaseId));
            if (listingId == Guid.Empty)
                throw new ArgumentException("Listing ID is required.", nameof(listingId));
            if (tenantAccountId == Guid.Empty || landlordAccountId == Guid.Empty)
                throw new ArgumentException("Lease accounts are required.");
            if (rentMinor <= 0)
                throw new ArgumentOutOfRangeException(nameof(rentMinor));
            if (periodDays <= 0)
                throw new ArgumentOutOfRangeException(nameof(periodDays));
            if (endsAt <= startsAt)
                throw new ArgumentOutOfRangeException(nameof(endsAt));

            LeaseId = leaseId;
            ListingId = listingId;
            TenantAccountId = tenantAccountId;
            LandlordAccountId = landlordAccountId;
            RentMinor = rentMinor;
            PeriodDays = periodDays;
            StartsAt = startsAt;
            EndsAt = endsAt;
            NextDueAt = nextDueAt;
            Active = active;
            LastPaymentTransactionId = lastPaymentTransactionId;
        }

        public Guid LeaseId { get; }
        public Guid ListingId { get; }
        public Guid TenantAccountId { get; }
        public Guid LandlordAccountId { get; }
        public long RentMinor { get; }
        public int PeriodDays { get; }
        public DateTimeOffset StartsAt { get; }
        public DateTimeOffset EndsAt { get; }
        public DateTimeOffset NextDueAt { get; }
        public bool Active { get; }
        public Guid LastPaymentTransactionId { get; }

        public NexLandLease Advance(Guid transactionId, DateTimeOffset nextDueAt, bool active) =>
            new NexLandLease(
                LeaseId,
                ListingId,
                TenantAccountId,
                LandlordAccountId,
                RentMinor,
                PeriodDays,
                StartsAt,
                EndsAt,
                nextDueAt,
                active,
                transactionId);
    }

    public sealed class NexBankStatement
    {
        public NexBankStatement(
            Guid accountId,
            DateTimeOffset from,
            DateTimeOffset to,
            long openingBalance,
            long closingBalance,
            IReadOnlyList<NexLedgerTransaction> transactions)
        {
            AccountId = accountId;
            From = from;
            To = to;
            OpeningBalance = openingBalance;
            ClosingBalance = closingBalance;
            Transactions = transactions ?? Array.Empty<NexLedgerTransaction>();
        }

        public Guid AccountId { get; }
        public DateTimeOffset From { get; }
        public DateTimeOffset To { get; }
        public long OpeningBalance { get; }
        public long ClosingBalance { get; }
        public IReadOnlyList<NexLedgerTransaction> Transactions { get; }
    }

    public sealed class NexReconciliationReport
    {
        public NexReconciliationReport(
            Guid accountId,
            long derivedBalance,
            long recomputedBalance,
            int transactionCount,
            bool balanced)
        {
            AccountId = accountId;
            DerivedBalance = derivedBalance;
            RecomputedBalance = recomputedBalance;
            TransactionCount = transactionCount;
            Balanced = balanced;
        }

        public Guid AccountId { get; }
        public long DerivedBalance { get; }
        public long RecomputedBalance { get; }
        public int TransactionCount { get; }
        public bool Balanced { get; }
    }

    public interface INexEconomyWorkflowStore
    {
        NexAccountTransferPolicy GetTransferPolicy(Guid accountId);
        NexAccountTransferPolicy SetTransferPolicy(NexAccountTransferPolicy policy);
        long GetOutgoingTotal(Guid accountId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive);

        NexPaymentRequest CreatePaymentRequest(NexPaymentRequest request);
        NexPaymentRequest GetPaymentRequest(Guid requestId);
        IReadOnlyList<NexPaymentRequest> ListPaymentRequests(Guid accountId, int offset, int limit);
        NexPaymentRequest UpdatePaymentRequest(NexPaymentRequest request);

        NexCommerceOrder CreateCommerceOrder(NexCommerceOrder order);
        NexCommerceOrder GetCommerceOrder(Guid orderId);
        IReadOnlyList<NexCommerceOrder> ListCommerceOrders(Guid accountId, int offset, int limit);
        NexCommerceOrder UpdateCommerceOrder(NexCommerceOrder order);

        NexLandListing CreateLandListing(NexLandListing listing);
        NexLandListing GetLandListing(Guid listingId);
        IReadOnlyList<NexLandListing> SearchLandListings(string query, NexLandListingType? type, int offset, int limit);
        NexLandListing UpdateLandListing(NexLandListing listing);

        NexLandLease CreateLandLease(NexLandLease lease);
        NexLandLease GetLandLease(Guid leaseId);
        IReadOnlyList<NexLandLease> ListLandLeases(Guid accountId, int offset, int limit);
        NexLandLease UpdateLandLease(NexLandLease lease);
    }
}
