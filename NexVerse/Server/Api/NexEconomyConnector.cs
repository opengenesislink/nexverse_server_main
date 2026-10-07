// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using NexVerse.Core.Economy;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

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
        private static readonly Guid s_ConsoleGrantSystemAccountId =
            Guid.Parse("4e565324-434f-4e53-4f4c-450000000001");

        private IUserAccountService m_UserAccounts;
        private IInstantMessage m_InstantMessages;

        public static NexEconomyConnector Current { get; private set; }

        public bool Enabled { get; private set; }
        public string ProviderName { get; private set; } =
            string.Empty;
        public NexLedgerSqlStore Store { get; private set; }
        public NexDoubleEntryLedger Ledger { get; private set; }
        public NexEconomyService Economy { get; private set; }

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

            Store =
                store;
            Ledger =
                new NexDoubleEntryLedger(
                    store);
            Economy =
                new NexEconomyService(
                    Ledger,
                    store);
            ProviderName =
                runtime.ProviderName;
            Enabled =
                true;

            m_Log.InfoFormat(
                "[NEX-ECONOMY]: NV$-Ledger aktiv mit Provider {0}; Schema-Initialisierung {1}.",
                ProviderName,
                initializeSchema
                    ? "aktiv"
                    : "deaktiviert");

            InitializeConsoleAdministration(
                config);
        }

        private void InitializeConsoleAdministration(
            IConfigSource config)
        {
            if (MainConsole.Instance == null)
                return;

            try
            {
                m_UserAccounts =
                    LoadConfiguredService<IUserAccountService>(
                        config,
                        "UserAccountService");

                m_InstantMessages =
                    LoadConfiguredService<IInstantMessage>(
                        config,
                        "HGInstantMessageService");

                Economy.EnsureSystemAccount(
                    s_ConsoleGrantSystemAccountId,
                    "NV$ Console Administration");

                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse",
                    true,
                    "nv give",
                    "nv give \"<Avatarname|UUID>\" <Betrag> [Grund]",
                    "Gibt einem lokalen Avatar eine administrative NV$-Testgutschrift. Avatar-Namen mit Leerzeichen in Anfuehrungszeichen setzen.",
                    HandleNvGive);

                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse",
                    true,
                    "nv balance",
                    "nv balance \"<Avatarname|UUID>\"",
                    "Zeigt den autoritativen NV$-Kontostand eines lokalen Avatars.",
                    HandleNvBalance);

                m_Log.Info(
                    "[NEX-ECONOMY]: Konsolenbefehle 'nv give' und 'nv balance' sind aktiv.");
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-ECONOMY]: NV$-Konsolenverwaltung konnte nicht vollstaendig initialisiert werden: {0}",
                    e.Message);
            }
        }

        private void HandleNvGive(
            string module,
            string[] args)
        {
            if (args == null ||
                args.Length < 4)
            {
                MainConsole.Instance.Output(
                    "Syntax: nv give \"<Avatarname|UUID>\" <Betrag> [Grund]");
                return;
            }

            if (!long.TryParse(
                    args[3],
                    out long amount) ||
                amount <= 0 ||
                amount > int.MaxValue)
            {
                MainConsole.Instance.Output(
                    "[NV$] Ungueltiger Betrag. Erlaubt sind ganze Werte von 1 bis {0}.",
                    int.MaxValue);
                return;
            }

            UserAccount account =
                ResolveLocalAccount(
                    args[2],
                    out string resolveError);

            if (account == null)
            {
                MainConsole.Instance.Output(
                    "[NV$] {0}",
                    resolveError);
                return;
            }

            string reason =
                args.Length > 4
                    ? string.Join(
                        " ",
                        args.Skip(4))
                    : "Konsolen-Testgutschrift";

            reason =
                (reason ?? string.Empty)
                    .Trim();

            if (reason.Length == 0)
                reason = "Konsolen-Testgutschrift";

            if (reason.Length >
                NexLedgerAccountState.ReasonLengthLimit)
            {
                MainConsole.Instance.Output(
                    "[NV$] Grund ist zu lang. Maximal {0} Zeichen.",
                    NexLedgerAccountState.ReasonLengthLimit);
                return;
            }

            try
            {
                Guid residentId =
                    account.PrincipalID.Guid;

                Economy.EnsureResidentAccount(
                    residentId,
                    account.EffectiveDisplayName);

                NexLedgerAppendResult result =
                    Economy.AdministrativeAdjustment(
                        residentId,
                        s_ConsoleGrantSystemAccountId,
                        amount,
                        "robust-console",
                        reason,
                        "console-grant-" +
                        Guid.NewGuid().ToString("N"));

                long balance =
                    Economy.GetBalance(
                        residentId);

                bool notificationSent =
                    SendGrantNotification(
                        account,
                        amount,
                        balance,
                        reason);

                MainConsole.Instance.Output(
                    "[NV$] Gutschrift erfolgreich: {0} NV$ -> {1} ({2}). Neuer Kontostand: {3} NV$. Transaktion: {4}. Avatar-Meldung: {5}.",
                    amount,
                    account.EffectiveDisplayName,
                    account.PrincipalID,
                    balance,
                    result.Transaction.TransactionId,
                    notificationSent
                        ? "uebergeben"
                        : "nicht zugestellt");

                m_Log.InfoFormat(
                    "[NEX-ECONOMY]: Konsolen-Gutschrift: {0} NV$ an {1} ({2}); neuer Kontostand {3} NV$; Transaktion {4}; Grund: {5}",
                    amount,
                    account.EffectiveDisplayName,
                    account.PrincipalID,
                    balance,
                    result.Transaction.TransactionId,
                    reason);
            }
            catch (Exception e)
            {
                MainConsole.Instance.Output(
                    "[NV$] Gutschrift fehlgeschlagen: {0}",
                    e.Message);

                m_Log.WarnFormat(
                    "[NEX-ECONOMY]: Konsolen-Gutschrift fuer {0} fehlgeschlagen: {1}",
                    account.PrincipalID,
                    e.Message);
            }
        }

        private void HandleNvBalance(
            string module,
            string[] args)
        {
            if (args == null ||
                args.Length != 3)
            {
                MainConsole.Instance.Output(
                    "Syntax: nv balance \"<Avatarname|UUID>\"");
                return;
            }

            UserAccount account =
                ResolveLocalAccount(
                    args[2],
                    out string resolveError);

            if (account == null)
            {
                MainConsole.Instance.Output(
                    "[NV$] {0}",
                    resolveError);
                return;
            }

            try
            {
                Guid residentId =
                    account.PrincipalID.Guid;

                Economy.EnsureResidentAccount(
                    residentId,
                    account.EffectiveDisplayName);

                MainConsole.Instance.Output(
                    "[NV$] {0} ({1}): {2} NV$",
                    account.EffectiveDisplayName,
                    account.PrincipalID,
                    Economy.GetBalance(residentId));
            }
            catch (Exception e)
            {
                MainConsole.Instance.Output(
                    "[NV$] Kontostand konnte nicht gelesen werden: {0}",
                    e.Message);
            }
        }

        private UserAccount ResolveLocalAccount(
            string value,
            out string error)
        {
            error =
                string.Empty;

            if (m_UserAccounts == null)
            {
                error =
                    "UserAccountService ist fuer die NV$-Konsolenverwaltung nicht verfuegbar.";
                return null;
            }

            string query =
                (value ?? string.Empty)
                    .Trim();

            if (query.Length == 0)
            {
                error =
                    "Avatarname oder UUID fehlt.";
                return null;
            }

            if (UUID.TryParse(
                    query,
                    out UUID userId))
            {
                UserAccount byId =
                    m_UserAccounts.GetUserAccount(
                        UUID.Zero,
                        userId);

                if (byId == null)
                {
                    error =
                        "Kein lokaler Avatar mit UUID " +
                        userId.ToString() +
                        " gefunden.";
                }

                return byId;
            }

            int separator =
                query.IndexOf(' ');

            if (separator > 0 &&
                separator < query.Length - 1)
            {
                UserAccount direct =
                    m_UserAccounts.GetUserAccount(
                        UUID.Zero,
                        query.Substring(0, separator).Trim(),
                        query.Substring(separator + 1).Trim());

                if (direct != null)
                    return direct;
            }

            List<UserAccount> matches =
                m_UserAccounts.GetUserAccounts(
                    UUID.Zero,
                    query) ??
                new List<UserAccount>();

            List<UserAccount> exact =
                matches
                    .Where(
                        x =>
                            x != null &&
                            (
                                string.Equals(
                                    x.Name?.Trim(),
                                    query,
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(
                                    x.Username?.Trim(),
                                    query,
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(
                                    x.EffectiveDisplayName?.Trim(),
                                    query,
                                    StringComparison.OrdinalIgnoreCase)
                            ))
                    .ToList();

            if (exact.Count == 1)
                return exact[0];

            if (matches.Count == 1)
                return matches[0];

            if (matches.Count > 1)
            {
                error =
                    "Mehrere Avatare gefunden. Bitte UUID oder exakten Namen verwenden: " +
                    string.Join(
                        ", ",
                        matches
                            .Take(5)
                            .Select(
                                x =>
                                    x.Name +
                                    " (" +
                                    x.PrincipalID +
                                    ")"));
                return null;
            }

            error =
                "Kein lokaler Avatar fuer '" +
                query +
                "' gefunden.";
            return null;
        }

        private bool SendGrantNotification(
            UserAccount account,
            long amount,
            long balance,
            string reason)
        {
            if (m_InstantMessages == null ||
                account == null)
            {
                return false;
            }

            try
            {
                GridInstantMessage message =
                    new GridInstantMessage
                    {
                        imSessionID =
                            UUID.Random().Guid,
                        fromAgentID =
                            Constants.servicesGodAgentID.Guid,
                        toAgentID =
                            account.PrincipalID.Guid,
                        timestamp =
                            (uint)Util.UnixTimeSinceEpoch(),
                        fromAgentName =
                            "NexVerse Bank",
                        message =
                            "NV$-Testgutschrift: " +
                            amount +
                            " NV$ wurden deinem Konto gutgeschrieben. Neuer Kontostand: " +
                            balance +
                            " NV$. Grund: " +
                            reason,
                        dialog =
                            (byte)InstantMessageDialog.MessageFromAgent,
                        fromGroup =
                            false,
                        offline =
                            (byte)0,
                        ParentEstateID =
                            0,
                        Position =
                            Vector3.Zero,
                        RegionID =
                            UUID.Zero.Guid,
                        binaryBucket =
                            new byte[] { 0 }
                    };

                return
                    m_InstantMessages.IncomingInstantMessage(
                        message);
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-ECONOMY]: Avatar-Meldung fuer Konsolen-Gutschrift an {0} konnte nicht zugestellt werden: {1}",
                    account.PrincipalID,
                    e.Message);

                return false;
            }
        }

        private static T LoadConfiguredService<T>(
            IConfigSource config,
            string sectionName)
            where T : class
        {
            IConfig section =
                config?.Configs[sectionName];

            string module =
                section?.GetString(
                    "LocalServiceModule",
                    string.Empty)
                    ?.Trim()
                ?? string.Empty;

            if (module.Length == 0)
                return null;

            return
                ServerUtils.LoadPlugin<T>(
                    module,
                    new object[] { config });
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
