// SPDX-License-Identifier: MPL-2.0

using System;
using log4net;
using Nini.Config;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    public sealed class NexVerseWorldApiConnector : ServiceConnector
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(NexVerseWorldApiConnector));

        public NexVerseWorldApiConnector(IConfigSource config, IHttpServer server, string configName)
            : base(config, server, configName)
        {
            string sectionName = string.IsNullOrWhiteSpace(configName) ? "NexVerseWorldApi" : configName;
            IConfig apiConfig = config.Configs[sectionName];

            if (apiConfig == null)
                throw new InvalidOperationException("Missing [" + sectionName + "] configuration section.");

            if (!apiConfig.GetBoolean("Enabled", true))
            {
                m_Log.Info("[NEX-WORLD-API]: World API connector is disabled.");
                return;
            }

            string publicBaseUrl = apiConfig
                .GetString("PublicBaseUrl", "http://world.stadt-nexverse.de")
                .TrimEnd('/');

            INexEventBus eventBus = new InMemoryNexEventBus();
            INexAuditSink auditSink = new LogNexAuditSink();
            INexAuthorizationService authorization = new NexAuthorizationService();
            NexVerseWorldApiHandlers handlers = new NexVerseWorldApiHandlers(publicBaseUrl, eventBus, auditSink);

            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1", handlers.Root, "NexVerse World API"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/health", handlers.Health, "NexVerse World API Health"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/version", handlers.Version, "NexVerse World API Version"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/capabilities", handlers.Capabilities, "NexVerse World API Capabilities"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/openapi.json", handlers.OpenApi, "NexVerse World API OpenAPI"));

            bool privilegedEndpoints = apiConfig.GetBoolean("EnablePrivilegedEndpoints", false);
            if (privilegedEndpoints)
            {
                IConfig userConfig = config.Configs["UserAccountService"];
                IConfig authConfig = config.Configs["AuthenticationService"];

                string userModule = apiConfig.GetString(
                    "UserAccountServiceModule",
                    userConfig == null ? string.Empty : userConfig.GetString("LocalServiceModule", string.Empty));

                string authModule = apiConfig.GetString(
                    "AuthenticationServiceModule",
                    authConfig == null ? string.Empty : authConfig.GetString("LocalServiceModule", string.Empty));

                if (string.IsNullOrWhiteSpace(userModule) || string.IsNullOrWhiteSpace(authModule))
                    throw new InvalidOperationException("NexVerse privileged API requires UserAccountServiceModule and AuthenticationServiceModule.");

                IUserAccountService userAccounts =
                    ServerUtils.LoadPlugin<IUserAccountService>(userModule, new object[] { config });

                if (userAccounts == null)
                    throw new InvalidOperationException("Unable to load NexVerse World API user account service.");

                IAuthenticationService authentication =
                    ServerUtils.LoadPlugin<IAuthenticationService>(authModule, new object[] { config, userAccounts });

                if (authentication == null)
                    authentication = ServerUtils.LoadPlugin<IAuthenticationService>(authModule, new object[] { config });

                if (authentication == null)
                    throw new InvalidOperationException("Unable to load NexVerse World API authentication service.");

                IInventoryService inventory = LoadOptionalService<IInventoryService>(config, "InventoryService");
                IGridUserService gridUsers = LoadOptionalService<IGridUserService>(config, "GridUserService");
                IGridService grid = LoadOptionalService<IGridService>(config, "GridService");

                int adminMinimumLevel = apiConfig.GetInt("AdminMinimumUserLevel", 200);
                int tokenLifetimeSeconds = apiConfig.GetInt("TokenLifetimeSeconds", 1800);

                INexAccessTokenService nativeTokens = null;
                if (apiConfig.GetBoolean("EnableNativeTokens", false))
                {
                    string signingKey = apiConfig.GetString("NativeTokenSigningKey", string.Empty);
                    string issuer = apiConfig.GetString("NativeTokenIssuer", publicBaseUrl);
                    string audience = apiConfig.GetString("NativeTokenAudience", "nexverse-world-api");

                    nativeTokens = new HmacNexAccessTokenService(
                        issuer,
                        audience,
                        signingKey,
                        tokenLifetimeSeconds);

                    m_Log.Info("[NEX-WORLD-API]: NexVerse native scoped access tokens are enabled.");
                }

                NexApiAuthenticator authenticator = new NexApiAuthenticator(
                    authentication,
                    userAccounts,
                    authorization,
                    nativeTokens,
                    adminMinimumLevel,
                    tokenLifetimeSeconds);

                OpenSimNexUserService userService = new OpenSimNexUserService(
                    userAccounts,
                    authentication,
                    inventory,
                    gridUsers,
                    grid);

                NexUserApiRouter userRouter = new NexUserApiRouter(
                    userService,
                    authenticator,
                    eventBus,
                    auditSink,
                    nativeTokens);

                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler("/api", userRouter.Handle, "NexVerse World API privileged router"),
                    true);

                m_Log.Warn("[NEX-WORLD-API]: Privileged endpoints are enabled. Use only over a transport that protects bearer tokens.");
            }
            else
            {
                m_Log.Info("[NEX-WORLD-API]: Privileged endpoints are disabled.");
            }

            m_Log.InfoFormat("[NEX-WORLD-API]: World API {0} enabled at {1}/api/v1", Core.NexVersePlatform.ApiVersion, publicBaseUrl);
        }

        private static T LoadOptionalService<T>(IConfigSource config, string sectionName)
            where T : class
        {
            IConfig section = config.Configs[sectionName];
            if (section == null)
                return null;

            string module = section.GetString("LocalServiceModule", string.Empty);
            if (string.IsNullOrWhiteSpace(module))
                return null;

            try
            {
                return ServerUtils.LoadPlugin<T>(module, new object[] { config });
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-WORLD-API]: Optional service {0} could not be loaded: {1}",
                    sectionName,
                    e.Message);
                return null;
            }
        }
    }

    internal sealed class LogNexAuditSink : INexAuditSink
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(LogNexAuditSink));

        public void Record(NexAuditEvent auditEvent)
        {
            if (auditEvent == null)
                return;

            m_Log.InfoFormat(
                "[NEX-AUDIT]: id={0} actor={1} action={2} resource={3} correlation={4}",
                auditEvent.EventId,
                auditEvent.Actor,
                auditEvent.Action,
                auditEvent.Resource,
                auditEvent.CorrelationId);
        }
    }
}
