#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
errors = []

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def require(path, *needles):
    text = read(path)
    for needle in needles:
        if needle not in text:
            errors.append(f"{path}: missing {needle!r}")
    return text

worker = require(
    "NexVerse/Server/Api/OglDatabaseMaintenanceWorker.cs",
    'JobType => "database.maintenance"',
    '"analyze"',
    "SemaphoreSlim",
    "DbConnection",
    "INFORMATION_SCHEMA.TABLES",
    "pg_catalog.pg_tables",
    "sqlite_master",
    "ANALYZE TABLE ",
    "BuildAnalyzeSql",
    "SanitizeProviderMessage",
    "Password",
    "Pwd",
    "OperationCanceledException",
    '["arbitrary_sql"]',
)

api = require(
    "NexVerse/Server/Api/OglJobsApi.cs",
    '"/api/v1/jobs/database/maintenance"',
    '"database.maintenance"',
    '"mode"',
    '"dry_run"',
)

connector = require(
    "NexVerse/Server/Api/NexVerseWorldApiConnector.cs",
    'config.Configs["DatabaseService"]',
    '"StorageProvider"',
    '"ConnectionString"',
    "new OglDatabaseMaintenanceWorker(",
    '"DatabaseMaintenanceCommandTimeoutSeconds"',
)

for path in ("bin/Robust.ini.example", "bin/Robust.HG.ini.example"):
    require(path, "DatabaseMaintenanceCommandTimeoutSeconds = 300")

docs = require(
    "doc/NexVerse/WORLD_API.md",
    "/api/v1/jobs/database/maintenance",
    "database.maintenance",
    "MySQL/MariaDB",
    "PostgreSQL",
    "SQLite",
    "arbitrary SQL",
)

roadmap = require(
    "doc/NexVerse/ROADMAP.md",
    "- [x] region migration;",
    "- [x] backup;",
    "- [x] restore;",
    "- [x] asset reindex;",
    "- [x] database maintenance.",
    "- [x] progress;",
)

# The HTTP contract must not accept caller-provided SQL or table identifiers.
queue_start = api.find("private void QueueDatabaseMaintenance(")
auth_start = api.find("private bool Authenticate(", queue_start)
queue = api[queue_start:auth_start] if queue_start >= 0 and auth_start > queue_start else ""
for forbidden in ('GetProperty("sql"', 'TryGetProperty("sql"', 'GetProperty("table"', 'TryGetProperty("table"'):
    if forbidden in queue:
        errors.append("OglJobsApi.cs: database maintenance accepts forbidden caller-controlled SQL/table input")

# The worker may describe dangerous operations in comments/docs, but it must
# build executable maintenance SQL only through the fixed ANALYZE builder.
if "command.CommandText = sql;" not in worker:
    errors.append("OglDatabaseMaintenanceWorker.cs: expected bounded command execution path missing")
if 'Optional(parameters, "sql"' in worker:
    errors.append("OglDatabaseMaintenanceWorker.cs: arbitrary SQL parameter detected")

if errors:
    print("[OGL-DATABASE-MAINTENANCE] FEHLER")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("[OGL-DATABASE-MAINTENANCE] OK")
