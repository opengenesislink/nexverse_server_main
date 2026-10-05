// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NexVerse.Core.Jobs;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Performs bounded, provider-aware maintenance against the authoritative
    /// [DatabaseService] connection. This worker never accepts SQL from API
    /// callers. OpenGenesisLINK 0.9.3.5 deliberately exposes only ANALYZE,
    /// because compaction/rebuild operations can take disruptive locks on a
    /// live grid and require a separate maintenance-window contract.
    /// </summary>
    internal sealed class OglDatabaseMaintenanceWorker : IOglJobWorker
    {
        private static readonly SemaphoreSlim s_MaintenanceGate =
            new SemaphoreSlim(1, 1);

        private readonly string m_StorageProvider;
        private readonly string m_ConnectionString;
        private readonly int m_CommandTimeoutSeconds;

        public OglDatabaseMaintenanceWorker(
            string storageProvider,
            string connectionString,
            int commandTimeoutSeconds)
        {
            m_StorageProvider = storageProvider ?? string.Empty;
            m_ConnectionString = connectionString ?? string.Empty;
            m_CommandTimeoutSeconds =
                Math.Clamp(commandTimeoutSeconds, 10, 3600);
        }

        public string JobType => "database.maintenance";

        public async Task<IReadOnlyDictionary<string, string>> ExecuteAsync(
            OglJobContext context,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            string mode =
                Optional(parameters, "mode", "analyze")
                    .Trim()
                    .ToLowerInvariant();

            bool dryRun =
                OptionalBool(parameters, "dry_run");

            if (!string.Equals(
                    mode,
                    "analyze",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Nicht unterstuetzte Datenbank-Wartung. In OpenGenesisLINK 0.9.3.5 ist nur 'analyze' erlaubt.");
            }

            if (string.IsNullOrWhiteSpace(m_StorageProvider))
                throw new InvalidOperationException(
                    "[DatabaseService] StorageProvider ist nicht konfiguriert.");

            if (string.IsNullOrWhiteSpace(m_ConnectionString))
                throw new InvalidOperationException(
                    "[DatabaseService] ConnectionString ist nicht konfiguriert.");

            DatabaseBackend backend =
                DatabaseBackend.FromStorageProvider(
                    m_StorageProvider);

            context.Progress(
                2,
                "waiting",
                "Exklusive OpenGenesisLINK DB-Wartung wird angefordert.");

            await s_MaintenanceGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                context.Progress(
                    5,
                    "connecting",
                    "Verbindung zum konfigurierten Datenbank-Backend wird aufgebaut.");

                using DbConnection connection =
                    backend.CreateConnection(
                        m_ConnectionString);

                try
                {
                    await connection
                        .OpenAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    throw ProviderFailure(
                        backend.Name,
                        "Verbindungsaufbau",
                        e);
                }

                cancellationToken.ThrowIfCancellationRequested();

                string databaseName =
                    await ReadDatabaseNameAsync(
                            connection,
                            backend,
                            cancellationToken)
                        .ConfigureAwait(false);

                context.Log(
                    "Datenbank-Backend: " +
                    backend.Name +
                    "; Datenbank: " +
                    databaseName +
                    ".");

                context.Progress(
                    12,
                    "inventory",
                    "Anwendungstabellen werden erfasst.");

                IReadOnlyList<DatabaseTable> tables =
                    await ReadTablesAsync(
                            connection,
                            backend,
                            cancellationToken)
                        .ConfigureAwait(false);

                context.Log(
                    tables.Count +
                    " Anwendungstabellen fuer ANALYZE erfasst.");

                if (dryRun)
                {
                    for (int i = 0; i < tables.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int progress =
                            15 +
                            (int)(80L * (i + 1) /
                                  Math.Max(1, tables.Count));

                        context.Progress(
                            progress,
                            "dry_run",
                            tables[i].DisplayName);
                    }

                    context.Progress(
                        97,
                        "dry_run_completed",
                        "Dry-Run abgeschlossen; keine Datenbankstatistik wurde veraendert.");

                    stopwatch.Stop();

                    return Result(
                        backend.Name,
                        databaseName,
                        mode,
                        tables.Count,
                        0,
                        stopwatch.ElapsedMilliseconds,
                        true);
                }

                int analyzed = 0;

                for (int i = 0; i < tables.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DatabaseTable table = tables[i];

                    int progress =
                        15 +
                        (int)(80L * i /
                              Math.Max(1, tables.Count));

                    context.Progress(
                        progress,
                        "analyzing",
                        table.DisplayName);

                    string sql =
                        backend.BuildAnalyzeSql(table);

                    try
                    {
                        await ExecuteMaintenanceStatementAsync(
                                connection,
                                sql,
                                m_CommandTimeoutSeconds,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        throw ProviderFailure(
                            backend.Name,
                            "ANALYZE fuer " + table.DisplayName,
                            e);
                    }

                    analyzed++;

                    context.Progress(
                        15 +
                        (int)(80L * analyzed /
                              Math.Max(1, tables.Count)),
                        "analyzing",
                        table.DisplayName);
                }

                context.Progress(
                    97,
                    "verifying",
                    "Datenbank-Wartung wurde providerseitig abgeschlossen.");

                stopwatch.Stop();

                context.Log(
                    "ANALYZE abgeschlossen: " +
                    analyzed +
                    " Tabellen in " +
                    stopwatch.ElapsedMilliseconds +
                    " ms.");

                return Result(
                    backend.Name,
                    databaseName,
                    mode,
                    tables.Count,
                    analyzed,
                    stopwatch.ElapsedMilliseconds,
                    false);
            }
            finally
            {
                stopwatch.Stop();
                s_MaintenanceGate.Release();
            }
        }

        private async Task<string> ReadDatabaseNameAsync(
            DbConnection connection,
            DatabaseBackend backend,
            CancellationToken cancellationToken)
        {
            if (backend.Kind == DatabaseBackendKind.Sqlite)
                return "main";

            string sql =
                backend.Kind == DatabaseBackendKind.MySql
                    ? "SELECT DATABASE()"
                    : "SELECT current_database()";

            using DbCommand command =
                connection.CreateCommand();

            command.CommandText = sql;
            command.CommandTimeout =
                m_CommandTimeoutSeconds;

            object value =
                await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);

            string name =
                Convert.ToString(value) ?? string.Empty;

            return string.IsNullOrWhiteSpace(name)
                ? backend.Name
                : name.Trim();
        }

        private async Task<IReadOnlyList<DatabaseTable>> ReadTablesAsync(
            DbConnection connection,
            DatabaseBackend backend,
            CancellationToken cancellationToken)
        {
            string sql =
                backend.Kind switch
                {
                    DatabaseBackendKind.MySql =>
                        "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' " +
                        "ORDER BY TABLE_NAME",

                    DatabaseBackendKind.PostgreSql =>
                        "SELECT schemaname, tablename FROM pg_catalog.pg_tables " +
                        "WHERE schemaname NOT IN ('pg_catalog','information_schema') " +
                        "AND schemaname NOT LIKE 'pg_toast%' " +
                        "ORDER BY schemaname, tablename",

                    _ =>
                        "SELECT name FROM sqlite_master " +
                        "WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                        "ORDER BY name"
                };

            List<DatabaseTable> tables =
                new List<DatabaseTable>();

            using DbCommand command =
                connection.CreateCommand();

            command.CommandText = sql;
            command.CommandTimeout =
                m_CommandTimeoutSeconds;

            using DbDataReader reader =
                await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

            while (await reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (backend.Kind ==
                    DatabaseBackendKind.PostgreSql)
                {
                    string schema =
                        Convert.ToString(reader.GetValue(0))
                        ?? string.Empty;

                    string name =
                        Convert.ToString(reader.GetValue(1))
                        ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(schema) &&
                        !string.IsNullOrWhiteSpace(name))
                    {
                        tables.Add(
                            new DatabaseTable(
                                schema.Trim(),
                                name.Trim()));
                    }
                }
                else
                {
                    string name =
                        Convert.ToString(reader.GetValue(0))
                        ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        tables.Add(
                            new DatabaseTable(
                                string.Empty,
                                name.Trim()));
                    }
                }
            }

            return tables;
        }

        private static async Task ExecuteMaintenanceStatementAsync(
            DbConnection connection,
            string sql,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            using DbCommand command =
                connection.CreateCommand();

            command.CommandText = sql;
            command.CommandTimeout =
                timeoutSeconds;

            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () =>
                    {
                        try
                        {
                            command.Cancel();
                        }
                        catch
                        {
                        }
                    });

            using DbDataReader reader =
                await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

            do
            {
                while (await reader
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                }
            }
            while (await reader
                .NextResultAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        private Exception ProviderFailure(
            string provider,
            string operation,
            Exception error)
        {
            string message =
                SanitizeProviderMessage(
                    error?.Message ?? "Unbekannter Datenbankfehler.");

            return new InvalidOperationException(
                operation +
                " ist auf " +
                provider +
                " fehlgeschlagen: " +
                message);
        }

        private string SanitizeProviderMessage(string message)
        {
            string safe =
                message ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(m_ConnectionString))
            {
                safe = safe.Replace(
                    m_ConnectionString,
                    "<connection-string-redacted>",
                    StringComparison.Ordinal);
            }

            try
            {
                DbConnectionStringBuilder builder =
                    new DbConnectionStringBuilder
                    {
                        ConnectionString =
                            m_ConnectionString
                    };

                foreach (string key in builder.Keys)
                {
                    if (!IsSecretKey(key))
                        continue;

                    string secret =
                        Convert.ToString(builder[key])
                        ?? string.Empty;

                    if (secret.Length >= 3)
                    {
                        safe = safe.Replace(
                            secret,
                            "***",
                            StringComparison.Ordinal);
                    }
                }
            }
            catch
            {
            }

            return safe;
        }

        private static bool IsSecretKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            return
                key.Equals(
                    "Password",
                    StringComparison.OrdinalIgnoreCase) ||
                key.Equals(
                    "Pwd",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> Result(
            string provider,
            string database,
            string mode,
            int tableCount,
            int analyzedTables,
            long elapsedMilliseconds,
            bool dryRun)
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = provider ?? string.Empty,
                ["database"] = database ?? string.Empty,
                ["mode"] = mode ?? string.Empty,
                ["table_count"] = tableCount.ToString(),
                ["analyzed_tables"] =
                    analyzedTables.ToString(),
                ["elapsed_ms"] =
                    elapsedMilliseconds.ToString(),
                ["dry_run"] =
                    dryRun.ToString(),
                ["arbitrary_sql"] =
                    bool.FalseString
            };
        }

        private static string Optional(
            IReadOnlyDictionary<string, string> parameters,
            string key,
            string fallback)
        {
            if (parameters != null &&
                parameters.TryGetValue(
                    key,
                    out string value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return fallback;
        }

        private static bool OptionalBool(
            IReadOnlyDictionary<string, string> parameters,
            string key)
        {
            return
                parameters != null &&
                parameters.TryGetValue(
                    key,
                    out string raw) &&
                bool.TryParse(
                    raw,
                    out bool value) &&
                value;
        }

        private sealed class DatabaseTable
        {
            public DatabaseTable(
                string schema,
                string name)
            {
                Schema = schema ?? string.Empty;
                Name = name ?? string.Empty;
            }

            public string Schema { get; }
            public string Name { get; }

            public string DisplayName =>
                string.IsNullOrWhiteSpace(Schema)
                    ? Name
                    : Schema + "." + Name;
        }

        private enum DatabaseBackendKind
        {
            MySql,
            PostgreSql,
            Sqlite
        }

        private sealed class DatabaseBackend
        {
            private DatabaseBackend(
                DatabaseBackendKind kind,
                string name,
                string connectionType)
            {
                Kind = kind;
                Name = name;
                ConnectionType = connectionType;
            }

            public DatabaseBackendKind Kind { get; }
            public string Name { get; }
            private string ConnectionType { get; }

            public static DatabaseBackend FromStorageProvider(
                string storageProvider)
            {
                string provider =
                    storageProvider?.Trim()
                    ?? string.Empty;

                if (provider.IndexOf(
                        "MySQL",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new DatabaseBackend(
                        DatabaseBackendKind.MySql,
                        "mysql",
                        "MySql.Data.MySqlClient.MySqlConnection, MySql.Data");
                }

                if (provider.IndexOf(
                        "PGSQL",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    provider.IndexOf(
                        "Postgre",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new DatabaseBackend(
                        DatabaseBackendKind.PostgreSql,
                        "postgresql",
                        "Npgsql.NpgsqlConnection, Npgsql");
                }

                if (provider.IndexOf(
                        "SQLite",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new DatabaseBackend(
                        DatabaseBackendKind.Sqlite,
                        "sqlite",
                        "Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite");
                }

                throw new NotSupportedException(
                    "Datenbank-Wartung unterstuetzt in OpenGenesisLINK 0.9.3.5 nur MySQL/MariaDB, PostgreSQL und SQLite. StorageProvider: " +
                    provider);
            }

            public DbConnection CreateConnection(
                string connectionString)
            {
                Type type =
                    Type.GetType(
                        ConnectionType,
                        false);

                if (type == null)
                {
                    throw new InvalidOperationException(
                        "Datenbank-Provider konnte nicht geladen werden: " +
                        Name +
                        ".");
                }

                if (!(Activator.CreateInstance(type) is
                    DbConnection connection))
                {
                    throw new InvalidOperationException(
                        "Datenbank-Provider stellt keine DbConnection bereit: " +
                        Name +
                        ".");
                }

                connection.ConnectionString =
                    connectionString;

                return connection;
            }

            public string BuildAnalyzeSql(
                DatabaseTable table)
            {
                if (table == null ||
                    string.IsNullOrWhiteSpace(table.Name))
                {
                    throw new ArgumentException(
                        "Ungueltige Datenbanktabelle.");
                }

                if (Kind ==
                    DatabaseBackendKind.MySql)
                {
                    return
                        "ANALYZE TABLE " +
                        QuoteMySql(table.Name);
                }

                if (Kind ==
                    DatabaseBackendKind.PostgreSql)
                {
                    return
                        "ANALYZE " +
                        QuoteSql(table.Schema) +
                        "." +
                        QuoteSql(table.Name);
                }

                return
                    "ANALYZE " +
                    QuoteSql(table.Name);
            }

            private static string QuoteMySql(
                string identifier)
            {
                return
                    "`" +
                    identifier.Replace(
                        "`",
                        "``",
                        StringComparison.Ordinal) +
                    "`";
            }

            private static string QuoteSql(
                string identifier)
            {
                return
                    "\"" +
                    identifier.Replace(
                        "\"",
                        "\"\"",
                        StringComparison.Ordinal) +
                    "\"";
            }
        }
    }
}
