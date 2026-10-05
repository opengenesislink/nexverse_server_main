#!/usr/bin/env python3
from pathlib import Path

connector = Path("NexVerse/Server/Api/NexEconomyConnector.cs").read_text(encoding="utf-8")

for marker in (
    "public sealed class NexEconomyConnector : ServiceConnector",
    'string.IsNullOrWhiteSpace(configName)',
    '"NexEconomy"',
    'GetBoolean(\n                    "Enabled",\n                    false)',
    'config.Configs["DatabaseService"]',
    'EffectiveValue(',
    '"StorageProvider"',
    '"ConnectionString"',
    'storageProvider.IndexOf(',
    '"Null"',
    "NexLedgerSqlRuntime.Resolve(",
    "runtime.CreateStore(",
    "new NexDoubleEntryLedger(",
    "new NexEconomyService(",
    "public NexEconomyService Economy",
    "public static NexEconomyConnector Current",
):
    assert marker in connector, f"missing NexEconomy runtime binding marker: {marker}"

for forbidden in (
    "AddSimpleStreamHandler(",
    "SimpleStreamHandler(",
    "connectionString +",
    "ConnectionString +",
    "m_Log.Info(connectionString",
    "m_Log.InfoFormat(connectionString",
):
    assert forbidden not in connector, f"economy bootstrap leaked transport/secret behavior: {forbidden}"

for path in (
    "bin/Robust.ini.example",
    "bin/Robust.HG.ini.example",
    "bin/Robust.HG.ini",
):
    text = Path(path).read_text(encoding="utf-8")
    assert 'NexEconomyConnector = "NexEconomy@' in text, f"missing connector registration: {path}"
    assert "[NexEconomy]" in text, f"missing NexEconomy section: {path}"
    section = text.split("[NexEconomy]", 1)[1].split("\n[", 1)[0]
    assert "Enabled = false" in section, f"NexEconomy must default disabled: {path}"
    assert 'StorageProvider = ""' in section, f"NexEconomy must inherit provider by default: {path}"
    assert 'ConnectionString = ""' in section, f"NexEconomy must inherit connection by default: {path}"
    assert "InitializeSchema = true" in section, f"NexEconomy schema bootstrap setting missing: {path}"

ci = Path("bin/Robust.NexEconomy.Tests.ini").read_text(encoding="utf-8")
assert "Enabled = true" in ci
assert 'StorageProvider = "OpenSim.Data.SQLite.dll"' in ci
assert "/tmp/opengenesislink-economy-ci.db" in ci

print("NV$ Robust runtime configuration binding: OK")
