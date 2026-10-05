#!/usr/bin/env python3
from pathlib import Path

workflow = Path(".github/workflows/nexjast-ci.yml").read_text(encoding="utf-8")
program = Path("tools/ci/NexLedgerSqlMatrixRegression/Program.cs").read_text(encoding="utf-8")
project = Path("tools/ci/NexLedgerSqlMatrixRegression/NexLedgerSqlMatrixRegression.csproj").read_text(encoding="utf-8")

for marker in (
    "ledger-db-matrix:",
    "image: mariadb:11.4",
    "image: postgres:16",
    "33306:3306",
    "35432:5432",
    "NEX_LEDGER_MARIADB:",
    "NEX_LEDGER_POSTGRESQL:",
    "NexLedgerSqlMatrixRegression.csproj",
):
    assert marker in workflow, f"missing live ledger matrix workflow marker: {marker}"

for marker in (
    "NexLedgerSqlRuntime.Resolve(",
    '"OpenSim.Data.MySQL.dll"',
    '"OpenSim.Data.PGSQL.dll"',
    '"mysql"',
    '"postgresql"',
    "runtime.CreateStore()",
    "reopened.Post(issuance).Status",
    "NexLedgerAppendStatus.Duplicate",
    "VerifyProviderRollback(",
    "transaction.Rollback()",
    "rollback left a transaction header behind",
    "rejected transaction changed balance",
    "CurrentSchemaVersion",
):
    assert marker in program, f"missing live ledger matrix regression marker: {marker}"

for marker in (
    "../../../bin/MySql.Data.dll",
    "../../../bin/Npgsql.dll",
    "../../../bin/",
    "NexLedgerSqlStore.cs",
    "NexLedgerSqlRuntime.cs",
):
    assert marker in project, f"missing matrix project provider/runtime marker: {marker}"

print("NV$ live MariaDB/PostgreSQL matrix contract: OK")
