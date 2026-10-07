#!/usr/bin/env python3
from pathlib import Path

model = Path("NexVerse/Core/Economy/NexLedgerAccountState.cs").read_text(encoding="utf-8")
virtual = Path("NexVerse/Core/Economy/NexVirtualBankAccount.cs").read_text(encoding="utf-8")
workflow = Path("NexVerse/Core/Economy/NexEconomyWorkflow.cs").read_text(encoding="utf-8")
service = Path("NexVerse/Core/Economy/NexEconomyService.cs").read_text(encoding="utf-8")
memory = Path("NexVerse/Core/Economy/NexLedgerStore.cs").read_text(encoding="utf-8")
sql = Path("NexVerse/Core/Economy/NexLedgerSqlStore.cs").read_text(encoding="utf-8")
regression = Path("tools/ci/NexEconomyPolicyRegression/Program.cs").read_text(encoding="utf-8")

for marker in (
    "NexLedgerAccountStatus",
    "Active = 1",
    "Locked = 2",
    "Closed = 3",
    "NexLedgerAccountStateEvent",
    "INexLedgerAccountStateStore",
):
    assert marker in model, f"missing account lifecycle marker: {marker}"

for marker in (
    'public const string Scheme = "NVBAN"',
    'public const string Prefix = "NVBAN-"',
    "CreateIdentifier(",
    "IsValidIdentifier(",
    "INexVirtualBankAccountStore",
):
    assert marker in virtual, f"missing virtual account marker: {marker}"

for marker in (
    "NexPaymentRequest",
    "NexAccountTransferPolicy",
    "NexCommerceOrder",
    "NexLandListing",
    "NexLandLease",
    "NexBankStatement",
    "NexReconciliationReport",
    "INexEconomyWorkflowStore",
):
    assert marker in workflow, f"missing banking/commerce model marker: {marker}"

for marker in (
    "public sealed class NexEconomyService",
    "EnsureResidentAccount(",
    "EnsureSystemAccount(",
    "AdministrativeAdjustment(",
    "Reverse(",
    "SetAccountStatus(",
    "EnsureVirtualBankAccount(",
    "ResolveVirtualBankAccount(",
    "BankingTransfer(",
    "CreatePaymentRequest(",
    "PayPaymentRequest(",
    "RefundCommerceOrder(",
    "FundEscrow(",
    "ReleaseEscrow(",
    "GetStatement(",
    "ReconcileAccount(",
    "PurchaseLandListing(",
    "CreateLandLease(",
    "PayLandRent(",
    "ExecuteCommerceOrder(",
    "Insufficient NV$ balance",
    "Closed ledger accounts cannot be reopened",
    "A ledger account can be closed only with zero balance",
    '"reversal_of"',
    "DeterministicGuid(",
):
    assert marker in service, f"missing economy policy marker: {marker}"

for marker in (
    "INexLedgerAccountStateStore",
    "m_AccountStates",
    "m_AccountStateEvents",
    "m_VirtualBankAccountsByAccount",
    "GetOrCreateVirtualBankAccount(",
    "INexEconomyWorkflowStore",
    "m_TransferPolicies",
    "m_PaymentRequests",
    "m_CommerceOrders",
    "m_LandListings",
    "m_LandLeases",
):
    assert marker in memory, f"missing in-memory lifecycle marker: {marker}"

for marker in (
    "CurrentSchemaVersion = 4",
    "ogl_ledger_account_state",
    "ogl_ledger_account_events",
    "ApplySchemaVersion2(",
    "ApplySchemaVersion3(",
    "ApplySchemaVersion4(",
    "ogl_ledger_virtual_accounts",
    "ogl_economy_transfer_policies",
    "ogl_economy_payment_requests",
    "ogl_economy_commerce_orders",
    "ogl_economy_land_listings",
    "ogl_economy_land_leases",
    "GetOrCreateVirtualBankAccount(",
    "SetAccountStatus(",
    "ListAccountStateEvents(",
    "UNIQUE (account_id, state_version)",
):
    assert marker in sql, f"missing SQL lifecycle marker: {marker}"

for forbidden in (
    "UPDATE ogl_ledger_transactions",
    "DELETE FROM ogl_ledger_transactions",
    "UPDATE ogl_ledger_postings",
    "DELETE FROM ogl_ledger_postings",
    "UPDATE ogl_ledger_account_events",
    "DELETE FROM ogl_ledger_account_events",
):
    assert forbidden not in sql, f"append-only ledger contract violated: {forbidden}"

for marker in (
    "fraud review",
    "payment rollback",
    "merchant retired",
    "long-reason adjustment roundtrip mismatch",
    "schema version mismatch",
    "resident NVBAN mapping mismatch",
    "NVBAN mappings were not persisted",
    "banking transfer/fee balances mismatch",
    "payment request settlement mismatch",
    "reconciliation failed",
    "escrow balances mismatch",
    "commerce refund mismatch",
    "land purchase mismatch",
    "land lease initial settlement mismatch",
):
    assert marker in regression, f"missing policy regression marker: {marker}"

print("NV$ banking, commerce, land and policy contract: OK")
