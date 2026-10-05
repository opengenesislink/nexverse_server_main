// SPDX-License-Identifier: MPL-2.0

using System;
using log4net;
using NexVerse.Core.Economy;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Robust bootstrap for the NV$ ledger. This connector exposes no HTTP
    /// routes and no Viewer money protocol. It only binds the durable ledger
    /// to explicit runtime configuration.
    /// </summary>
    public sealed class NexEconomyConnector : ServiceConnector
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexEconomyConnector));
        private static readonly object s_Sync =
            new object();

        public static NexEconomyConnector Current { get; private set; }

        public bool Enabled { get; private set; }
        public string ProviderName { get; private set; } =
            string.Empty;
        public NexDoubleEntryLedger Ledger { get; private set; }

        public NexEconomyConnector(
            IConfigSource config,
            IHttpServer server,
            string configName)
            : base(
                config,
                server,
                configName)
        {
            string sectionName =
                string.IsNullOrWhiteSpace(configName)
                    ? "NexEconomy"
                    : configName;

            IConfig economy =
                config.Configs[sectionName];

            lock (s_Sync)
                Current = this;

            if (economy == null ||
                !economy.GetBoolean(
                    "Enabled",
                    false))
            {
                m_Log.Info(
                    "[NEX-ECONOMY]: NV$ ledger runtime is disabled.");
                return;
            }

            IConfig database =
                config.Configs["DatabaseService"];

            string storageProvider =
                EffectiveValue(
                    economy,
                    database,
                    "StorageProvider");

            string connectionString =
                EffectiveValue(
                    economy,
                    database,
                    "ConnectionString");

            if (storageProvider.IndexOf(
                    "Null",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    "[NEX-ECONOMY]: Null storage is not valid for the durable NV$ ledger.");
            }

            bool initializeSchema =
                economy.GetBoolean(
                    "InitializeSchema",
                    true);

            NexLedgerSqlRuntime runtime =
                NexLedgerSqlRuntime.Resolve(
                    storageProvider,
                    connectionString);

            NexLedgerSqlStore store =
                runtime.CreateStore(
                    initializeSchema);

            Ledger =
                new NexDoubleEntryLedger(
                    store);
            ProviderName =
                runtime.ProviderName;
            Enabled =
                true;

            m_Log.InfoFormat(
                "[NEX-ECONOMY]: NV$ ledger runtime enabled with provider {0}; schema initialization {1}.",
                ProviderName,
                initializeSchema
                    ? "enabled"
                    : "disabled");
        }

        private static string EffectiveValue(
            IConfig primary,
            IConfig fallback,
            string key)
        {
            string value =
                primary?.GetString(
                    key,
                    string.Empty)
                    ?.Trim()
                ?? string.Empty;

            if (value.Length > 0)
                return value;

            return
                fallback?.GetString(
                    key,
                    string.Empty)
                    ?.Trim()
                ?? string.Empty;
        }
    }
}
