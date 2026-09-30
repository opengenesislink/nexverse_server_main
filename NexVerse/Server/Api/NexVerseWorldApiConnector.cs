// SPDX-License-Identifier: MPL-2.0

using System;
using log4net;
using Nini.Config;
using NexVerse.Core.Audit;
using NexVerse.Core.Messaging;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

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
            NexVerseWorldApiHandlers handlers = new NexVerseWorldApiHandlers(publicBaseUrl, eventBus, auditSink);

            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1", handlers.Root, "NexVerse World API"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/health", handlers.Health, "NexVerse World API Health"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/version", handlers.Version, "NexVerse World API Version"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/capabilities", handlers.Capabilities, "NexVerse World API Capabilities"));
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/api/v1/openapi.json", handlers.OpenApi, "NexVerse World API OpenAPI"));

            m_Log.InfoFormat("[NEX-WORLD-API]: World API {0} enabled at {1}/api/v1", Core.NexVersePlatform.ApiVersion, publicBaseUrl);
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
