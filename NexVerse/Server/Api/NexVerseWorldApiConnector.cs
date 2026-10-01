// SPDX-License-Identifier: MPL-2.0

using System;
using log4net;
using Nini.Config;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using NexVerse.Core.Observability;
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

            NexMetricsRegistry metrics = NexMetricsRegistry.Default;
            metrics.SetGauge(
                "nexverse_build_info",
                "NexVerse build metadata.",
                1,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["api_version"] = Core.NexVersePlatform.ApiVersion,
                    ["milestone"] = Core.NexVersePlatform.MilestoneCodename
                });

            IConfig metricsConfig = config.Configs["NexMetrics"];
            if (metricsConfig != null && metricsConfig.GetBoolean("Enabled", false))
            {
                string metricsPath = metricsConfig.GetString("Path", "/internal/metrics");
                NexMetricsEndpoint metricsEndpoint = new NexMetricsEndpoint(metrics);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(metricsPath, metricsEndpoint.Handle, "NexVerse Prometheus Metrics"));
                m_Log.WarnFormat("[NEX-METRICS]: Metrics endpoint enabled at {0}. Restrict access with firewall/TLS policy.", metricsPath);
            }

            INexEventBus eventBus = CreateEventBus(config, server);
            INexAuditSink publicAuditSink = new LogNexAuditSink();
            INexAuthorizationService authorization = new NexAuthorizationService();

            NexApiRequestGate apiGate = new NexApiRequestGate(
                apiConfig.GetBoolean("RateLimitEnabled", true),
                apiConfig.GetInt("RateLimitRequests", 240),
                apiConfig.GetInt("RateLimitWindowSeconds", 60),
                apiConfig.GetBoolean("TrustForwardedFor", false));

            NexVerseWorldApiHandlers handlers = new NexVerseWorldApiHandlers(publicBaseUrl, eventBus, publicAuditSink);
            NexApiDocsPage docsPage = new NexApiDocsPage();

            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1", apiGate.Wrap(handlers.Root), "NexVerse World API"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/docs", apiGate.Wrap(docsPage.Handle), "NexVerse World API Explorer"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/health", apiGate.Wrap(handlers.Health), "NexVerse World API Health"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/version", apiGate.Wrap(handlers.Version), "NexVerse World API Version"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/capabilities", apiGate.Wrap(handlers.Capabilities), "NexVerse World API Capabilities"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/openapi.json", apiGate.Wrap(handlers.OpenApi), "NexVerse World API OpenAPI"));

            bool privilegedEndpoints = apiConfig.GetBoolean("EnablePrivilegedEndpoints", false);
            if (privilegedEndpoints)
            {
                string auditStorePath = apiConfig.GetString(
                    "AuditStorePath",
                    "data/nexverse-audit.jsonl");

                INexAuditStore auditStore =
                    new PersistentNexAuditStore(auditStorePath);

                string apiKeyStorePath =
                    apiConfig.GetString(
                        "ApiKeyStorePath",
                        "data/nexverse-api-keys.json");
                INexApiKeyStore apiKeyStore =
                    new PersistentNexApiKeyStore(
                        apiKeyStorePath);

                string idempotencyStorePath =
                    apiConfig.GetString(
                        "IdempotencyStorePath",
                        "data/nexverse-idempotency.json");
                int idempotencyTtlSeconds =
                    apiConfig.GetInt(
                        "IdempotencyTtlSeconds",
                        86400);

                INexIdempotencyStore idempotencyStore =
                    new PersistentNexIdempotencyStore(
                        idempotencyStorePath);

                INexAuditSink auditSink =
                    new CompositeNexAuditSink(
                        publicAuditSink,
                        auditStore);

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
                INexOAuthStore oauthStore = null;
                INexOidcSigningService oidcSigner = null;
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

                    string authStorePath = apiConfig.GetString(
                        "AuthStorePath",
                        "data/nexverse-auth.json");
                    string oidcIssuer = apiConfig.GetString("OidcIssuer", publicBaseUrl);
                    string oidcKeyPath = apiConfig.GetString(
                        "OidcSigningKeyPath",
                        "data/nexverse-oidc-es256.pem");
                    int oidcTokenLifetimeSeconds = apiConfig.GetInt(
                        "OidcTokenLifetimeSeconds",
                        tokenLifetimeSeconds);

                    oauthStore = new PersistentNexOAuthStore(authStorePath);
                    oidcSigner = new PersistentEs256OidcSigningService(
                        oidcIssuer,
                        oidcKeyPath,
                        oidcTokenLifetimeSeconds);

                    m_Log.Info("[NEX-WORLD-API]: NexVerse native scoped access tokens and persistent OAuth/OIDC are enabled.");
                }

                NexApiAuthenticator authenticator = new NexApiAuthenticator(
                    authentication,
                    userAccounts,
                    authorization,
                    nativeTokens,
                    oauthStore,
                    apiKeyStore,
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
                    nativeTokens,
                    oauthStore,
                    auditStore,
                    idempotencyStore,
                    idempotencyTtlSeconds,
                    apiKeyStore);

                if (nativeTokens != null && oauthStore != null && oidcSigner != null)
                {
                    int authCodeLifetimeSeconds = apiConfig.GetInt("AuthorizationCodeLifetimeSeconds", 120);
                    int refreshLifetimeSeconds = apiConfig.GetInt("RefreshTokenLifetimeSeconds", 2592000);

                    NexOAuthApiRouter oauthRouter = new NexOAuthApiRouter(
                        publicBaseUrl,
                        nativeTokens,
                        oidcSigner,
                        oauthStore,
                        authenticator,
                        userAccounts,
                        eventBus,
                        auditSink,
                        authCodeLifetimeSeconds,
                        refreshLifetimeSeconds);

                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/.well-known/openid-configuration",
                        apiGate.Wrap(oauthRouter.Discovery),
                        "NexVerse OIDC Discovery"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/oauth/jwks",
                        apiGate.Wrap(oauthRouter.Jwks),
                        "NexVerse OIDC JWKS"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/oauth/authorize",
                        apiGate.Wrap(oauthRouter.Authorize),
                        "NexVerse OAuth Authorization"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/oauth/token",
                        apiGate.Wrap(oauthRouter.Token),
                        "NexVerse OAuth Token"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/oauth/revoke",
                        apiGate.Wrap(oauthRouter.Revoke),
                        "NexVerse OAuth Revocation"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/api/v1/auth/clients",
                        apiGate.Wrap(oauthRouter.Clients),
                        "NexVerse OAuth Client Administration"));
                    server.AddSimpleStreamHandler(new SimpleStreamHandler(
                        "/api/v1/auth/sessions/revoke",
                        apiGate.Wrap(oauthRouter.RevokeSessions),
                        "NexVerse Session Revocation"));
                }

                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler("/api", apiGate.Wrap(userRouter.Handle), "NexVerse World API privileged router"),
                    true);

                m_Log.Warn("[NEX-WORLD-API]: Privileged endpoints are enabled. Use only over a transport that protects bearer tokens.");
            }
            else
            {
                m_Log.Info("[NEX-WORLD-API]: Privileged endpoints are disabled.");
            }

            m_Log.InfoFormat("[NEX-WORLD-API]: World API {0} enabled at {1}/api/v1", Core.NexVersePlatform.ApiVersion, publicBaseUrl);
        }

        private static INexEventBus CreateEventBus(IConfigSource config, IHttpServer server)
        {
            IConfig busConfig = config.Configs["NexBus"];
            if (busConfig == null || !busConfig.GetBoolean("Enabled", false))
            {
                m_Log.Info("[NEXBUS]: Distributed transport is disabled; using in-memory event bus.");
                return new InMemoryNexEventBus();
            }

            string nodeId = busConfig.GetString("NodeId", Environment.MachineName);
            string sharedKey = busConfig.GetString("SharedKey", string.Empty);
            string peersRaw = busConfig.GetString("Peers", string.Empty);
            string inboundPath = busConfig.GetString("InboundPath", "/internal/nexbus/v1/events");
            int queueCapacity = busConfig.GetInt("QueueCapacity", 4096);
            int timeoutMs = busConfig.GetInt("RequestTimeoutMilliseconds", 2000);
            int deduplicationWindow = busConfig.GetInt("DeduplicationWindow", 10000);

            string[] peers = peersRaw.Split(
                new[] { ';', ',' },
                StringSplitOptions.RemoveEmptyEntries);

            HttpNexEventTransport transport = new HttpNexEventTransport(
                nodeId,
                peers,
                sharedKey,
                queueCapacity,
                timeoutMs);

            DistributedNexEventBus distributed = new DistributedNexEventBus(
                transport,
                deduplicationWindow);

            NexBusHttpEndpoint endpoint = new NexBusHttpEndpoint(distributed, sharedKey);
            server.AddSimpleStreamHandler(
                new SimpleStreamHandler(inboundPath, endpoint.Handle, "NexBus peer transport"));

            m_Log.WarnFormat(
                "[NEXBUS]: Distributed HTTP transport enabled for node {0} with {1} configured peer(s). Protect this transport with TLS/firewall policy.",
                nodeId,
                peers.Length);

            return distributed;
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
