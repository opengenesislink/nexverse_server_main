#!/usr/bin/env python3
from pathlib import Path

model = Path("NexVerse/Core/Economy/NexLedgerModel.cs").read_text(encoding="utf-8")
store = Path("NexVerse/Core/Economy/NexLedgerStore.cs").read_text(encoding="utf-8")
roadmap = Path("doc/NexVerse/ROADMAP.md").read_text(encoding="utf-8")
docs = Path("doc/NexVerse/ECONOMY_LEDGER.md").read_text(encoding="utf-8")

for marker in (
    'public const string Code = "NVD"',
    'public const string Symbol = "NV$"',
    "public const int MinorUnits = 0",
    "Resident = 1",
    "Group = 2",
    "Business = 3",
    "Estate = 4",
    "ObjectMerchantEndpoint = 5",
    "System = 6",
    "Escrow = 7",
    "public enum NexLedgerSide",
    "public sealed class NexLedgerPosting",
    "public sealed class NexLedgerTransaction",
    "PostingCountLimit = 128",
    "MetadataEntryLimit = 32",
    "MetadataValueLengthLimit = 1024",
    "MemoLengthLimit = 255",
    "Enum.IsDefined(typeof(NexLedgerAccountClass), accountClass)",
    "if (debit != credit)",
    "at least two accounts",
    "Posting IDs must be unique",
):
    assert marker in model, f"missing ledger model marker: {marker}"

for marker in (
    "public interface INexLedgerStore",
    "public sealed class InMemoryNexLedgerStore",
    "NexLedgerAppendStatus.Duplicate",
    "Transaction ID already exists with different immutable content",
    "Posting ID",
    "Cross-currency posting is not allowed",
    "checked(current + delta)",
    "public sealed class NexDoubleEntryLedger",
):
    assert marker in store, f"missing ledger store marker: {marker}"

assert "- [ ] Durable transactional SQL store" in roadmap
assert "InMemoryNexLedgerStore" in docs
assert "nicht" in docs and "produktiv" in docs
print("NV$ double-entry ledger foundation contract: OK")
