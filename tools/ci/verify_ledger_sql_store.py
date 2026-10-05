#!/usr/bin/env python3
from pathlib import Path

sql = Path("NexVerse/Core/Economy/NexLedgerSqlStore.cs").read_text(encoding="utf-8")
runtime = Path("NexVerse/Core/Economy/NexLedgerSqlRuntime.cs").read_text(encoding="utf-8")
regression = Path("tools/ci/NexLedgerRegression/Program.cs").read_text(encoding="utf-8")
project = Path("tools/ci/NexLedgerRegression/NexLedgerRegression.csproj").read_text(encoding="utf-8")

for marker in (
    "public sealed class NexLedgerSqlStore : INexLedgerStore",
    "public const int CurrentSchemaVersion = 1",
    "Func<DbConnection>",
    "IsolationLevel.Serializable",
    "ogl_ledger_accounts",
    "ogl_ledger_transactions",
    "ogl_ledger_postings",
    "UNIQUE (transaction_id, sequence_no)",
    "UNIQUE (account_id, posting_id)",
    "FOREIGN KEY (transaction_id)",
    "FOREIGN KEY (account_id)",
    "PRAGMA foreign_keys = ON",
    "SUM(",
    "CASE",
    "NexLedgerAppendStatus.Duplicate",
    "PostingExists(",
    "transaction.Commit()",
    "dbTransaction.Commit()",
):
    assert marker in sql, f"missing durable ledger marker: {marker}"

for forbidden in (
    "MySqlConnection",
    "NpgsqlConnection",
    "SqliteConnection",
    "balance_minor",
    "UPDATE ogl_ledger_postings",
    "DELETE FROM ogl_ledger_postings",
    "UPDATE ogl_ledger_transactions",
    "DELETE FROM ogl_ledger_transactions",
):
    assert forbidden not in sql, f"provider coupling or mutable journal operation found: {forbidden}"

for marker in (
    "public sealed class NexLedgerSqlRuntime",
    '"MySql.Data.MySqlClient.MySqlConnection, MySql.Data"',
    '"Npgsql.NpgsqlConnection, Npgsql"',
    '"Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite"',
    "NexLedgerSqlDialect.MySql",
    "NexLedgerSqlDialect.PostgreSql",
    "NexLedgerSqlDialect.Sqlite",
    "ResolveConnectionType()",
    "Assembly.Load(",
    "Assembly.LoadFrom(",
    "AppContext.BaseDirectory",
    "Activator.CreateInstance(type)",
    "connection.ConnectionString",
    "CreateStore(",
):
    assert marker in runtime, f"missing ledger runtime provider marker: {marker}"

for marker in (
    "RunSqliteRegression()",
    'NexLedgerSqlRuntime.Resolve(',
    '"OpenSim.Data.SQLite.dll"',
    "runtime.CreateStore()",
    "balance did not survive store reopen",
    "SQL identical retry was not idempotent",
    "SQL ledger schema version mismatch",
):
    assert marker in regression, f"missing SQL ledger regression marker: {marker}"

assert "Mono.Data.Sqlite.dll" in project
assert "NexLedgerSqlStore.cs" in project
assert "NexLedgerSqlRuntime.cs" in project

print("NV$ durable SQL ledger contract: OK")
