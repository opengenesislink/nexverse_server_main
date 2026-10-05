// SPDX-License-Identifier: MPL-2.0

using System;
using System.Data.Common;

namespace NexVerse.Core.Economy
{
    public sealed class NexLedgerSqlRuntime
    {
        private NexLedgerSqlRuntime(
            string storageProvider,
            string connectionString,
            NexLedgerSqlDialect dialect,
            string providerName,
            string connectionTypeName)
        {
            StorageProvider = storageProvider;
            ConnectionString = connectionString;
            Dialect = dialect;
            ProviderName = providerName;
            ConnectionTypeName = connectionTypeName;
        }

        public string StorageProvider { get; }
        public string ConnectionString { get; }
        public NexLedgerSqlDialect Dialect { get; }
        public string ProviderName { get; }
        public string ConnectionTypeName { get; }

        public static NexLedgerSqlRuntime Resolve(
            string storageProvider,
            string connectionString)
        {
            string provider =
                (storageProvider ?? string.Empty)
                    .Trim();
            string connection =
                (connectionString ?? string.Empty)
                    .Trim();

            if (provider.Length == 0)
            {
                throw new ArgumentException(
                    "Ledger StorageProvider is required.",
                    nameof(storageProvider));
            }

            if (connection.Length == 0)
            {
                throw new ArgumentException(
                    "Ledger ConnectionString is required.",
                    nameof(connectionString));
            }

            if (provider.IndexOf(
                    "MySQL",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                provider.IndexOf(
                    "MariaDB",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new NexLedgerSqlRuntime(
                    provider,
                    connection,
                    NexLedgerSqlDialect.MySql,
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
                return new NexLedgerSqlRuntime(
                    provider,
                    connection,
                    NexLedgerSqlDialect.PostgreSql,
                    "postgresql",
                    "Npgsql.NpgsqlConnection, Npgsql");
            }

            if (provider.IndexOf(
                    "SQLite",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new NexLedgerSqlRuntime(
                    provider,
                    connection,
                    NexLedgerSqlDialect.Sqlite,
                    "sqlite",
                    "Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite");
            }

            throw new NotSupportedException(
                "NV$ ledger supports MySQL/MariaDB, PostgreSQL and SQLite. StorageProvider: " +
                provider);
        }

        public DbConnection CreateConnection()
        {
            Type type =
                Type.GetType(
                    ConnectionTypeName,
                    false);

            if (type == null)
            {
                throw new InvalidOperationException(
                    "Ledger database provider could not be loaded: " +
                    ProviderName +
                    ".");
            }

            if (!(Activator.CreateInstance(type) is
                DbConnection connection))
            {
                throw new InvalidOperationException(
                    "Ledger database provider does not expose DbConnection: " +
                    ProviderName +
                    ".");
            }

            connection.ConnectionString =
                ConnectionString;

            return connection;
        }

        public NexLedgerSqlStore CreateStore(
            bool initializeSchema = true)
        {
            return new NexLedgerSqlStore(
                CreateConnection,
                Dialect,
                initializeSchema);
        }
    }
}
