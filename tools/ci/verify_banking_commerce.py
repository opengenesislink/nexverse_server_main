#!/usr/bin/env python3
from pathlib import Path

workflow = Path("NexVerse/Core/Economy/NexEconomyWorkflow.cs").read_text(encoding="utf-8")
service = Path("NexVerse/Core/Economy/NexEconomyService.cs").read_text(encoding="utf-8")
store = Path("NexVerse/Core/Economy/NexLedgerStore.cs").read_text(encoding="utf-8")
sql = Path("NexVerse/Core/Economy/NexLedgerSqlStore.cs").read_text(encoding="utf-8")
api = Path("NexVerse/Server/Api/NexBankingCommerceApi.cs").read_text(encoding="utf-8")
economy_api = Path("NexVerse/Server/Api/NexEconomyApi.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")
viewer = Path("NexVerse/RegionModules/Economy/NexVerseMoneyModule.cs").read_text(encoding="utf-8")
land = Path("OpenSim/Region/CoreModules/World/Land/LandManagementModule.cs").read_text(encoding="utf-8")
regression = Path("tools/ci/NexEconomyPolicyRegression/Program.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
world_api = Path("doc/NexVerse/WORLD_API.md").read_text(encoding="utf-8")

for marker in (
    "NexPaymentRequestStatus",
    "NexPaymentRequest",
    "NexAccountTransferPolicy",
    "NexCommerceKind",
    "VendorPayment",
    "ObjectSale",
    "MarketplacePurchase",
    "LandPurchase",
    "EventTicket",
    "Rental",
    "MerchantRefund",
    "NexCommerceOrder",
    "NexLandListing",
    "NexLandLease",
    "NexBankStatement",
    "NexReconciliationReport",
    "INexEconomyWorkflowStore",
):
    assert marker in workflow, f"missing workflow model marker: {marker}"

for marker in (
    "BankingTransfer(",
    "GetStatement(",
    "ReconcileAccount(",
    "SetTransferPolicy(",
    "CreatePaymentRequest(",
    "PayPaymentRequest(",
    "CancelPaymentRequest(",
    "Refund(",
    "FundEscrow(",
    "ReleaseEscrow(",
    "ExecuteCommerceOrder(",
    "RefundCommerceOrder(",
    "CreateLandListing(",
    "SearchLandListings(",
    "PurchaseLandListing(",
    "CreateLandLease(",
    "PayLandRent(",
    "EnsureWalletAccount(",
    "EnsureEscrowAccount(",
):
    assert marker in service, f"missing banking/commerce service marker: {marker}"

for marker in (
    "GetBalanceAt(",
    "ListTransactions(",
    "INexEconomyWorkflowStore",
    "m_TransferPolicies",
    "m_PaymentRequests",
    "m_CommerceOrders",
    "m_LandListings",
    "m_LandLeases",
):
    assert marker in store, f"missing workflow store marker: {marker}"

for marker in (
    "CurrentSchemaVersion = 4",
    "ApplySchemaVersion4(",
    "ogl_economy_transfer_policies",
    "ogl_economy_payment_requests",
    "ogl_economy_commerce_orders",
    "ogl_economy_land_listings",
    "ogl_economy_land_leases",
    "GetBalanceAt(",
    "ListTransactions(",
):
    assert marker in sql, f"missing SQL workflow marker: {marker}"

for forbidden in (
    "UPDATE ogl_ledger_transactions",
    "DELETE FROM ogl_ledger_transactions",
    "UPDATE ogl_ledger_postings",
    "DELETE FROM ogl_ledger_postings",
):
    assert forbidden not in sql, f"immutable journal violation: {forbidden}"

for marker in (
    "/api/v1/banking/transactions",
    "/api/v1/banking/statements",
    "/api/v1/banking/reconciliation",
    "/api/v1/banking/transfers",
    "/api/v1/banking/payment-requests",
    "/api/v1/banking/accounts/",
    "/api/v1/banking/escrow/",
    "/api/v1/commerce/orders",
    "/api/v1/land-commerce/listings",
    "/api/v1/land-commerce/leases",
    "economy.BankingTransfer(",
    "economy.CreatePaymentRequest(",
    "economy.PayPaymentRequest(",
    "economy.ExecuteCommerceOrder(",
    "economy.RefundCommerceOrder(",
    "economy.PurchaseLandListing(",
    "economy.CreateLandLease(",
    "economy.PayLandRent(",
):
    assert marker in api, f"missing World API banking/commerce marker: {marker}"

for forbidden in (
    "INexLedgerStore",
    "NexLedgerSqlStore",
    "DbConnection",
    ".Append(",
    "AdministrativeAdjustment(",
):
    assert forbidden not in api, f"banking/commerce API bypasses policy boundary: {forbidden}"

for marker in (
    '"/api/v1/banking"',
    '"/api/v1/commerce"',
    '"/api/v1/land-commerce"',
    "new NexBankingCommerceApi(",
):
    assert marker in connector, f"missing API registration marker: {marker}"

for marker in (
    '"/api/v1/economy/accounts/ensure"',
    "EnsureWalletAccount(",
    "economy.wallet.ensure",
):
    assert marker in economy_api, f"missing trusted wallet provisioning marker: {marker}"

for marker in (
    "client.OnObjectBuy +=",
    "private void ObjectBuy(",
    "CommercePaymentInternal(",
    "RefundCommerceInternal(",
    '"ObjectSale"',
    '"VendorPayment"',
    '"LandPurchase"',
    '"/api/v1/commerce/orders"',
    '"/api/v1/economy/accounts/ensure"',
    "TryResolveLocalGroup(",
    "TryResolveLocalObjectOwner(",
):
    assert marker in viewer, f"missing Viewer commerce marker: {marker}"

for forbidden in (
    "DbConnection",
    "NexLedgerSqlStore",
    "ConnectionString",
    "MySql",
    "Npgsql",
):
    assert forbidden not in viewer, f"Viewer adapter bypasses World API: {forbidden}"

for marker in (
    "if (!e.economyValidated)",
    "money.AmountCovered(",
    "money.MoveMoney(",
    "Land purchase:",
    "land.UpdateLandSold(",
):
    assert marker in land, f"missing native land settlement marker: {marker}"

assert land.index("money.MoveMoney(") < land.index("land.UpdateLandSold("),     "land ownership transfer occurs before economy settlement"

for marker in (
    "banking transfer/fee balances mismatch",
    "payment request settlement mismatch",
    "statement balances/history mismatch",
    "reconciliation failed",
    "escrow balances mismatch",
    "commerce refund mismatch",
    "land purchase mismatch",
    "land lease initial settlement mismatch",
):
    assert marker in regression, f"missing banking/commerce regression marker: {marker}"

for heading in (
    "### 10.3 Banking functions",
    "### 10.4 Viewer economy compatibility",
    "### 10.5 Land commerce",
    "### 10.6 NexCommerce",
):
    assert heading in roadmap, f"missing roadmap section: {heading}"

for marker in (
    "/api/v1/banking",
    "/api/v1/commerce",
    "/api/v1/land-commerce",
):
    assert marker in world_api, f"missing World API documentation marker: {marker}"

print("NV$ banking, Viewer commerce, land commerce and NexCommerce contract: OK")
