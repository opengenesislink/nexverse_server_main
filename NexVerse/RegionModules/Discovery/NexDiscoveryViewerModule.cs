// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace NexVerse.RegionModules.Discovery
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "NexDiscoveryViewerModule")]
    public sealed class NexDiscoveryViewerModule :
        ISharedRegionModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(
                typeof(NexDiscoveryViewerModule));

        private readonly object m_Sync = new object();
        private readonly List<Scene> m_Scenes =
            new List<Scene>();

        private bool m_Enabled;
        private string m_WorldApiBaseUrl = string.Empty;
        private int m_RequestTimeoutMilliseconds = 3000;
        private HttpClient m_Http;

        public string Name => "NexDiscoveryViewerModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig section =
                config?.Configs["NexDiscoveryViewer"];

            if (section == null ||
                !section.GetBoolean("Enabled", false))
            {
                m_Enabled = false;
                return;
            }

            m_WorldApiBaseUrl =
                section.GetString(
                        "WorldApiBaseUrl",
                        string.Empty)
                    .Trim()
                    .TrimEnd('/');

            m_RequestTimeoutMilliseconds =
                Math.Clamp(
                    section.GetInt(
                        "RequestTimeoutMilliseconds",
                        3000),
                    500,
                    30000);

            if (!Uri.TryCreate(
                    m_WorldApiBaseUrl,
                    UriKind.Absolute,
                    out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp &&
                 baseUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "[NEX-DISCOVERY]: WorldApiBaseUrl must be an absolute HTTP(S) URL.");
            }

            m_Http =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromMilliseconds(
                            m_RequestTimeoutMilliseconds)
                };

            m_Enabled = true;

            m_Log.InfoFormat(
                "[NEX-DISCOVERY]: Viewer capability adapter enabled for {0}.",
                m_WorldApiBaseUrl);
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            lock (m_Sync)
                m_Scenes.Add(scene);

            scene.EventManager.OnRegisterCaps +=
                OnRegisterCaps;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null)
                return;

            scene.EventManager.OnRegisterCaps -=
                OnRegisterCaps;

            lock (m_Sync)
                m_Scenes.Remove(scene);
        }

        public void Close()
        {
            lock (m_Sync)
            {
                foreach (Scene scene in m_Scenes)
                {
                    scene.EventManager.OnRegisterCaps -=
                        OnRegisterCaps;
                }

                m_Scenes.Clear();
            }

            m_Http?.Dispose();
            m_Http = null;
        }

        private void OnRegisterCaps(
            UUID agentId,
            Caps caps)
        {
            if (!m_Enabled ||
                caps == null)
            {
                return;
            }

            Register(
                caps,
                "NexSearch",
                "/api/v1/search");

            Register(
                caps,
                "NexPlaces",
                "/api/v1/places");

            Register(
                caps,
                "NexLandPortal",
                "/api/v1/land-portal");

            Register(
                caps,
                "NexDestinationGuide",
                "/api/v1/destinations");
        }

        private void Register(
            Caps caps,
            string capability,
            string worldApiPath)
        {
            caps.RegisterSimpleHandler(
                capability,
                new SimpleStreamHandler(
                    "/" + UUID.Random(),
                    (request, response) =>
                        Proxy(
                            request,
                            response,
                            worldApiPath)));
        }

        private void Proxy(
            IOSHttpRequest request,
            IOSHttpResponse response,
            string worldApiPath)
        {
            if (!string.Equals(
                    request?.HttpMethod,
                    "GET",
                    StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode =
                    (int)HttpStatusCode.MethodNotAllowed;
                response.ContentType =
                    "application/json; charset=utf-8";
                response.RawBuffer =
                    System.Text.Encoding.UTF8.GetBytes(
                        "{\"error\":\"method_not_allowed\",\"message\":\"GET is required.\"}");
                return;
            }

            try
            {
                string query =
                    request?.Url?.Query ??
                    string.Empty;

                using HttpRequestMessage outbound =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        m_WorldApiBaseUrl +
                        worldApiPath +
                        query);

                outbound.Headers.TryAddWithoutValidation(
                    "X-NexVerse-Viewer-Agent",
                    capsSafeAgent(request));

                using HttpResponseMessage upstream =
                    m_Http.Send(outbound);

                response.StatusCode =
                    (int)upstream.StatusCode;

                response.ContentType =
                    upstream.Content.Headers.ContentType
                        ?.ToString() ??
                    "application/json; charset=utf-8";

                response.RawBuffer =
                    upstream.Content
                        .ReadAsByteArrayAsync()
                        .GetAwaiter()
                        .GetResult();
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-DISCOVERY]: Viewer capability proxy {0} failed: {1}",
                    worldApiPath,
                    e.Message);

                response.StatusCode =
                    (int)HttpStatusCode.BadGateway;
                response.ContentType =
                    "application/json; charset=utf-8";
                response.RawBuffer =
                    System.Text.Encoding.UTF8.GetBytes(
                        "{\"error\":\"discovery_upstream_unavailable\",\"message\":\"World API discovery service is unavailable.\"}");
            }
        }

        private static string capsSafeAgent(
            IOSHttpRequest request)
        {
            string agent =
                request?.UserAgent ??
                string.Empty;

            return agent.Length <= 255
                ? agent
                : agent.Substring(0, 255);
        }
    }
}
