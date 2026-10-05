// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using log4net;
using Nini.Config;
using NexVerse.Core.Audit;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Economy;
using NexVerse.Core.Experiences;
using NexVerse.Core.Messaging;
using NexVerse.Core.Jobs;
using NexVerse.Core.Observability;
using NexVerse.Core.Security;
using OpenSim.Data;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Friends;
using OpenSim.Services.EstateService;

namespace NexVerse.Server.Api
{
    public sealed class NexVerseWorldApiConnector : ServiceConnector
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(NexVerseWorldApiConnector));
        private static readonly object s_TelemetrySync = new object();
        private static NexOtlpHttpExporter s_OtlpExporter;

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

            IConfig telemetryConfig = config.Configs["NexTelemetry"];
            if (telemetryConfig != null &&
                telemetryConfig.GetBoolean("Enabled", false))
            {
                string protocol =
                    telemetryConfig.GetString(
                        "Protocol",
                        "http/json").Trim();

                if (!string.Equals(
                        protocol,
                        "http/json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "NexVerse currently supports OTLP protocol http/json.");
                }

                lock (s_TelemetrySync)
                {
                    if (s_OtlpExporter == null)
                    {
                        string instanceId =
                            telemetryConfig.GetString(
                                "ServiceInstanceId",
                                string.Empty);

                        if (string.IsNullOrWhiteSpace(instanceId))
                            instanceId = Environment.MachineName;

                        NexOtlpHttpOptions options =
                            new NexOtlpHttpOptions
                            {
                                Endpoint =
                                    ParseOptionalAbsoluteUri(
                                        telemetryConfig.GetString(
                                            "Endpoint",
                                            "http://127.0.0.1:4318")),
                                TracesEndpoint =
                                    ParseOptionalAbsoluteUri(
                                        telemetryConfig.GetString(
                                            "TracesEndpoint",
                                            string.Empty)),
                                MetricsEndpoint =
                                    ParseOptionalAbsoluteUri(
                                        telemetryConfig.GetString(
                                            "MetricsEndpoint",
                                            string.Empty)),
                                ServiceName =
                                    telemetryConfig.GetString(
                                        "ServiceName",
                                        "NexVerse.Robust").Trim(),
                                ServiceInstanceId =
                                    instanceId.Trim(),
                                Headers =
                                    ParseOtlpHeaders(
                                        telemetryConfig.GetString(
                                            "Headers",
                                            string.Empty)),
                                ExportIntervalSeconds =
                                    telemetryConfig.GetInt(
                                        "ExportIntervalSeconds",
                                        10),
                                BatchSize =
                                    telemetryConfig.GetInt(
                                        "BatchSize",
                                        256),
                                QueueCapacity =
                                    telemetryConfig.GetInt(
                                        "QueueCapacity",
                                        4096),
                                TimeoutMilliseconds =
                                    telemetryConfig.GetInt(
                                        "TimeoutMilliseconds",
                                        10000),
                                MaxRetries =
                                    telemetryConfig.GetInt(
                                        "MaxRetries",
                                        3),
                                AllowInsecure =
                                    telemetryConfig.GetBoolean(
                                        "AllowInsecure",
                                        false)
                            };

                        s_OtlpExporter =
                            new NexOtlpHttpExporter(
                                metrics,
                                options);

                        AppDomain.CurrentDomain.ProcessExit +=
                            (_, __) =>
                            {
                                lock (s_TelemetrySync)
                                {
                                    s_OtlpExporter?.Dispose();
                                    s_OtlpExporter = null;
                                }
                            };

                        m_Log.InfoFormat(
                            "[NEX-OTLP]: OTLP/HTTP JSON export enabled for service {0}.",
                            options.ServiceName);
                    }
                }
            }

            INexEventBus eventBus = CreateEventBus(config, server);
            IConfig nexBusConfig = config.Configs["NexBus"];
            bool distributedNexBusEnabled =
                nexBusConfig != null &&
                nexBusConfig.GetBoolean("Enabled", false);
            int nodeStaleAfterSeconds =
                nexBusConfig == null
                    ? 90
                    : nexBusConfig.GetInt("NodeStaleAfterSeconds", 90);
            NexNodeRegistry nodeRegistry =
                new NexNodeRegistry(
                    eventBus,
                    nodeStaleAfterSeconds);

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

                NexRegionOperationRegistry regionOperationRegistry =
                    new NexRegionOperationRegistry(
                        eventBus,
                        auditSink);

                IOglJobStore jobStore = new PersistentOglJobStore(
                    apiConfig.GetString("JobStorePath", "data/opengenesislink-jobs.json"));
                OglJobEventBridge jobBridge = new OglJobEventBridge(jobStore, eventBus);
                OglJobRunner jobRunner = new OglJobRunner(jobStore);
                jobRunner.Register(new OglDirectoryBackupWorker());
                jobRunner.Register(new OglDirectoryRestoreWorker());
                IConfig assetServiceConfig = config.Configs["AssetService"];
                string assetRoot = assetServiceConfig == null
                    ? string.Empty
                    : assetServiceConfig.GetString("BaseDirectory", string.Empty);
                string assetIndexPath = apiConfig.GetString(
                    "AssetIndexPath",
                    "data/opengenesislink-assets-index.json");
                jobRunner.Register(new OglAssetReindexWorker(assetRoot, assetIndexPath));

                IConfig databaseServiceConfig = config.Configs["DatabaseService"];
                string databaseStorageProvider = databaseServiceConfig == null
                    ? string.Empty
                    : databaseServiceConfig.GetString("StorageProvider", string.Empty);
                string databaseConnectionString = databaseServiceConfig == null
                    ? string.Empty
                    : databaseServiceConfig.GetString("ConnectionString", string.Empty);
                jobRunner.Register(
                    new OglDatabaseMaintenanceWorker(
                        databaseStorageProvider,
                        databaseConnectionString,
                        apiConfig.GetInt("DatabaseMaintenanceCommandTimeoutSeconds", 300)));

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
                IAssetService assets = LoadOptionalService<IAssetService>(config, "AssetService");
                jobRunner.Register(new OglInventoryRepairWorker(inventory));

                IGridUserService gridUsers = LoadOptionalService<IGridUserService>(config, "GridUserService");
                IGridService grid = LoadOptionalService<IGridService>(config, "GridService");
                IEstateDataService estateData = LoadOptionalService<IEstateDataService>(config, "EstateDataStore");

                jobRunner.Register(
                    new OglRegionMigrationWorker(
                        nodeRegistry,
                        eventBus,
                        grid,
                        estateData,
                        apiConfig.GetInt("RegionMigrationOperationTimeoutSeconds", 1800)));

                IPresenceService presence = LoadOptionalService<IPresenceService>(config, "PresenceService");
                IGridUserData gridUserData = LoadGridUserData(config);

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
                    userAccounts,
                    authorization,
                    nativeTokens,
                    oauthStore,
                    apiKeyStore,
                    adminMinimumLevel);

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
                    apiKeyStore,
                    grid,
                    estateData,
                    userAccounts,
                    nodeRegistry,
                    regionOperationRegistry,
                    distributedNexBusEnabled,
                    adminMinimumLevel,
                    new PersistentNexSecurityStore(apiConfig.GetString("SecurityStorePath", "data/nexverse-security.json")));

                NexEconomyApi economyApi =
                    new NexEconomyApi(
                        authenticator,
                        userAccounts,
                        auditSink,
                        () =>
                            NexEconomyConnector.Current?.Enabled == true
                                ? NexEconomyConnector.Current.Economy
                                : null);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/economy",
                        apiGate.Wrap(economyApi.Handle),
                        "OpenGenesisLINK NV$ Economy API"),
                    true);

                NexBankingCommerceApi bankingCommerceApi =
                    new NexBankingCommerceApi(
                        authenticator,
                        userAccounts,
                        auditSink,
                        () =>
                            NexEconomyConnector.Current?.Enabled == true
                                ? NexEconomyConnector.Current.Economy
                                : null);

                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/banking",
                        apiGate.Wrap(bankingCommerceApi.HandleBanking),
                        "OpenGenesisLINK NV$ Banking API"),
                    true);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/commerce",
                        apiGate.Wrap(bankingCommerceApi.HandleCommerce),
                        "OpenGenesisLINK NexCommerce API"),
                    true);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/land-commerce",
                        apiGate.Wrap(bankingCommerceApi.HandleLandCommerce),
                        "OpenGenesisLINK Land Commerce API"),
                    true);

                IConfig experiencesConfig =
                    config.Configs["NexExperiences"];

                string experienceStorePath =
                    experiencesConfig?.GetString(
                        "StorePath",
                        "data/nexverse-experiences.json") ??
                    "data/nexverse-experiences.json";

                NexExperienceStore experienceStore =
                    new NexExperienceStore(
                        experienceStorePath);

                NexExperiencesApi experiencesApi =
                    new NexExperiencesApi(
                        authenticator,
                        userAccounts,
                        auditSink,
                        experienceStore);

                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/experiences",
                        apiGate.Wrap(experiencesApi.Handle),
                        "OpenGenesisLINK NexExperiences API"),
                    true);

                NexInventoryApi inventoryApi =
                    new NexInventoryApi(
                        inventory,
                        assets,
                        authenticator);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/inventory",
                        apiGate.Wrap(inventoryApi.Handle),
                        "NexVerse Inventory API"),
                    true);

                OglOarApi oarApi = new OglOarApi(authenticator, nodeRegistry, eventBus, jobBridge);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/oar",
                        apiGate.Wrap(oarApi.Handle),
                        "OpenGenesisLINK OAR API"),
                    true);

                OglIarApi iarApi = new OglIarApi(authenticator, nodeRegistry, userAccounts, eventBus, jobBridge);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/iar",
                        apiGate.Wrap(iarApi.Handle),
                        "OpenGenesisLINK IAR API"),
                    true);

                OglJobsApi jobsApi = new OglJobsApi(authenticator, jobStore, jobRunner);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/jobs",
                        apiGate.Wrap(jobsApi.Handle),
                        "OpenGenesisLINK Jobs API"),
                    true);

                NexStatisticsApi statisticsApi = new NexStatisticsApi(
                    userAccounts,
                    gridUserData,
                    presence,
                    grid,
                    authenticator,
                    true);

                INexSecurityStore securityStore =
                    new PersistentNexSecurityStore(
                        apiConfig.GetString("SecurityStorePath", "data/nexverse-security.json"));
                string webAuthnRpId = apiConfig.GetString("WebAuthnRpId", string.Empty);
                string webAuthnOrigin = apiConfig.GetString("WebAuthnOrigin", string.Empty);
                NexWebAuthnVerifier webAuthnVerifier =
                    string.IsNullOrWhiteSpace(webAuthnRpId) || string.IsNullOrWhiteSpace(webAuthnOrigin)
                        ? null
                        : new NexWebAuthnVerifier(webAuthnRpId, webAuthnOrigin);
                NexSecurityApi securityApi = new NexSecurityApi(authenticator, securityStore, webAuthnVerifier);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/security",
                        apiGate.Wrap(securityApi.Handle),
                        "NexVerse Security"));

                FriendsService friendsService = new FriendsService(config);
                NexSocialGraphApi socialGraph =
                    new NexSocialGraphApi(userAccounts, friendsService, authenticator, new MuteListService(config), auditSink, presence);
                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/relationships",
                        apiGate.Wrap(socialGraph.Handle),
                        "NexVerse Social Graph"));

                IConfig profilesConfig = config.Configs["UserProfilesService"];
                if (profilesConfig != null && profilesConfig.GetBoolean("Enabled", false))
                {
                    IConfig databaseConfig = config.Configs["DatabaseService"];
                    string profileStorageProvider =
                        profilesConfig.GetString(
                            "StorageProvider",
                            databaseConfig == null
                                ? string.Empty
                                : databaseConfig.GetString("StorageProvider", string.Empty));
                    string profileConnectionString =
                        profilesConfig.GetString(
                            "ConnectionString",
                            databaseConfig == null
                                ? string.Empty
                                : databaseConfig.GetString("ConnectionString", string.Empty));

                    IProfilesData profilesData =
                        ServerUtils.LoadPlugin<IProfilesData>(
                            profileStorageProvider,
                            new object[] { profileConnectionString });

                    if (profilesData == null)
                        throw new InvalidOperationException("Unable to load NexVerse WebProfileV3 profile datastore.");

                    NexWebProfileV3Api webProfileV3 =
                        new NexWebProfileV3Api(
                            userAccounts,
                            profilesData,
                            authenticator,
                            auditSink);

                    server.AddSimpleStreamHandler(
                        new SimpleStreamHandler(
                            "/api/v1/profiles",
                            apiGate.Wrap(webProfileV3.Handle),
                            "NexVerse WebProfileV3"));

                    m_Log.Info("[NEX-WEBPROFILE-V3]: WebProfileV3 enabled using the authoritative UserProfilesService store.");
                }

                server.AddSimpleStreamHandler(new SimpleStreamHandler(
                    "/api/v1/statistics/summary",
                    apiGate.Wrap(statisticsApi.Summary),
                    "NexVerse statistics summary"));

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
                        userService,
                        eventBus,
                        auditSink,
                        adminMinimumLevel,
                        authCodeLifetimeSeconds,
                        refreshLifetimeSeconds,
                        securityStore);

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
                IUserAccountService statisticsUserAccounts =
                    LoadOptionalService<IUserAccountService>(
                        config,
                        "UserAccountService");
                IGridUserData statisticsGridUsers =
                    LoadGridUserData(config);
                IPresenceService statisticsPresence =
                    LoadOptionalService<IPresenceService>(
                        config,
                        "PresenceService");
                IGridService statisticsGrid =
                    LoadOptionalService<IGridService>(
                        config,
                        "GridService");

                NexStatisticsApi publicStatisticsApi =
                    new NexStatisticsApi(
                        statisticsUserAccounts,
                        statisticsGridUsers,
                        statisticsPresence,
                        statisticsGrid,
                        null,
                        true);

                server.AddSimpleStreamHandler(
                    new SimpleStreamHandler(
                        "/api/v1/statistics/summary",
                        apiGate.Wrap(publicStatisticsApi.Summary),
                        "NexVerse aggregate statistics summary"));

                m_Log.Info(
                    "[NEX-WORLD-API]: Privileged endpoints are disabled; privacy-safe aggregate statistics remain available.");
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

        private static Uri ParseOptionalAbsoluteUri(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (!Uri.TryCreate(
                    value.Trim(),
                    UriKind.Absolute,
                    out Uri parsed))
            {
                throw new InvalidOperationException(
                    "Invalid NexTelemetry endpoint URL: " +
                    value);
            }

            return parsed;
        }

        private static IReadOnlyDictionary<string, string> ParseOtlpHeaders(
            string value)
        {
            Dictionary<string, string> headers =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(value))
                return headers;

            foreach (string rawPair in value.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = rawPair.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException(
                        "NexTelemetry Headers must use comma-separated key=value pairs.");
                }

                string key =
                    Uri.UnescapeDataString(
                        rawPair.Substring(
                            0,
                            separator).Trim());

                string headerValue =
                    Uri.UnescapeDataString(
                        rawPair.Substring(
                            separator + 1).Trim());

                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidOperationException(
                        "NexTelemetry header names must not be empty.");
                }

                headers[key] = headerValue;
            }

            return headers;
        }

        private static IGridUserData LoadGridUserData(IConfigSource config)
        {
            string dllName = string.Empty;
            string connectionString = string.Empty;
            string realm = "GridUser";

            IConfig database = config.Configs["DatabaseService"];
            if (database != null)
            {
                dllName = database.GetString("StorageProvider", dllName);
                connectionString = database.GetString("ConnectionString", connectionString);
            }

            IConfig gridUsers = config.Configs["GridUserService"];
            if (gridUsers != null)
            {
                dllName = gridUsers.GetString("StorageProvider", dllName);
                connectionString = gridUsers.GetString("ConnectionString", connectionString);
                realm = gridUsers.GetString("Realm", realm);
            }

            if (string.IsNullOrWhiteSpace(dllName))
            {
                m_Log.Warn("[NEX-WORLD-API]: Statistics GridUser data source is unavailable because no StorageProvider is configured.");
                return null;
            }

            try
            {
                return ServerUtils.LoadPlugin<IGridUserData>(
                    dllName,
                    new object[] { connectionString, realm });
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-WORLD-API]: Statistics GridUser data source could not be loaded: {0}",
                    e.Message);
                return null;
            }
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
