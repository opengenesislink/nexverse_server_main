// SPDX-License-Identifier: MPL-2.0

using System;
using System.Data.Common;
using System.IO;
using System.Reflection;

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
                ResolveConnectionType();

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

        private Type ResolveConnectionType()
        {
            Type direct =
                Type.GetType(
                    ConnectionTypeName,
                    false);

            if (direct != null)
                return direct;

            int separator =
                ConnectionTypeName.IndexOf(',');

            if (separator <= 0 ||
                separator >=
                    ConnectionTypeName.Length - 1)
            {
                throw new InvalidOperationException(
                    "Invalid ledger database connection type: " +
                    ProviderName +
                    ".");
            }

            string typeName =
                ConnectionTypeName
                    .Substring(
                        0,
                        separator)
                    .Trim();
            string assemblyName =
                ConnectionTypeName
                    .Substring(
                        separator + 1)
                    .Trim();

            foreach (Assembly loaded in
                     AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(
                        loaded.GetName().Name,
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type loadedType =
                    loaded.GetType(
                        typeName,
                        false);

                if (loadedType != null)
                    return loadedType;
            }

            try
            {
                Assembly referenced =
                    Assembly.Load(
                        new AssemblyName(
                            assemblyName));

                Type referencedType =
                    referenced.GetType(
                        typeName,
                        false);

                if (referencedType != null)
                    return referencedType;
            }
            catch
            {
            }

            string providerPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    assemblyName +
                    ".dll");

            if (File.Exists(providerPath))
            {
                try
                {
                    Assembly local =
                        Assembly.LoadFrom(
                            providerPath);

                    Type localType =
                        local.GetType(
                            typeName,
                            false);

                    if (localType != null)
                        return localType;
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "Ledger database provider could not be loaded: " +
                        ProviderName +
                        ".",
                        e);
                }
            }

            throw new InvalidOperationException(
                "Ledger database provider could not be loaded: " +
                ProviderName +
                ".");
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
