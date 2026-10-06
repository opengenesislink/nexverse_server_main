// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
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
        private bool m_LegacyFirestormCompatibility;
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

            m_LegacyFirestormCompatibility =
                section.GetBoolean(
                    "LegacyFirestormCompatibility",
                    true);

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
                "[NEX-DISCOVERY]: Viewer adapter enabled for {0}; Firestorm legacy bridge={1}.",
                m_WorldApiBaseUrl,
                m_LegacyFirestormCompatibility);
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

            if (m_LegacyFirestormCompatibility)
            {
                scene.EventManager.OnMakeRootAgent +=
                    OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent +=
                    OnMakeChildAgent;
            }
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
            scene.EventManager.OnMakeRootAgent -=
                OnMakeRootAgent;
            scene.EventManager.OnMakeChildAgent -=
                OnMakeChildAgent;

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
                    scene.EventManager.OnMakeRootAgent -=
                        OnMakeRootAgent;
                    scene.EventManager.OnMakeChildAgent -=
                        OnMakeChildAgent;
                }

                m_Scenes.Clear();
            }

            m_Http?.Dispose();
            m_Http = null;
        }

        private void OnMakeRootAgent(ScenePresence presence)
        {
            IClientAPI client = presence?.ControllingClient;
            if (!m_Enabled ||
                !m_LegacyFirestormCompatibility ||
                client == null)
            {
                return;
            }

            // Defensive detach first so a rootification retry cannot duplicate handlers.
            DetachLegacyHandlers(client);
            client.OnDirPlacesQuery += OnDirPlacesQuery;
            client.OnDirLandQuery += OnDirLandQuery;
            client.OnDirClassifiedQuery += OnDirClassifiedQuery;
        }

        private void OnMakeChildAgent(ScenePresence presence)
        {
            IClientAPI client = presence?.ControllingClient;
            if (client != null)
                DetachLegacyHandlers(client);
        }

        private void DetachLegacyHandlers(IClientAPI client)
        {
            client.OnDirPlacesQuery -= OnDirPlacesQuery;
            client.OnDirLandQuery -= OnDirLandQuery;
            client.OnDirClassifiedQuery -= OnDirClassifiedQuery;
        }

        private void OnDirPlacesQuery(
            IClientAPI remoteClient,
            UUID queryId,
            string queryText,
            int queryFlags,
            int category,
            string simName,
            int queryStart)
        {
            try
            {
                string path =
                    "/api/v1/places?q=" +
                    Escape(queryText) +
                    "&offset=" +
                    Math.Max(0, queryStart).ToString(CultureInfo.InvariantCulture) +
                    "&limit=100";

                using JsonDocument document = GetJson(path);
                List<DirPlacesReplyData> replies =
                    new List<DirPlacesReplyData>();

                if (document.RootElement.TryGetProperty(
                        "places",
                        out JsonElement places) &&
                    places.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement place in places.EnumerateArray())
                    {
                        if (!TryUuid(place, "parcel_id", out UUID parcelId))
                            continue;

                        string regionName = String(place, "region_name");
                        if (!string.IsNullOrWhiteSpace(simName) &&
                            !regionName.Contains(
                                simName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        replies.Add(
                            new DirPlacesReplyData
                            {
                                parcelID = parcelId,
                                name = String(place, "name"),
                                forSale = false,
                                auction = false,
                                dwell = Float(place, "traffic"),
                                Status = 0
                            });
                    }
                }

                remoteClient.SendDirPlacesReply(
                    queryId,
                    replies.ToArray());
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-DISCOVERY]: Firestorm Places query failed: {0}",
                    e.Message);
                remoteClient.SendDirPlacesReply(
                    queryId,
                    Array.Empty<DirPlacesReplyData>());
            }
        }

        private void OnDirLandQuery(
            IClientAPI remoteClient,
            UUID queryId,
            uint queryFlags,
            uint searchType,
            int price,
            int area,
            int queryStart)
        {
            try
            {
                string path =
                    "/api/v1/land-portal?type=sale&offset=" +
                    Math.Max(0, queryStart).ToString(CultureInfo.InvariantCulture) +
                    "&limit=100";

                if (price > 0)
                {
                    path +=
                        "&max_price=" +
                        price.ToString(CultureInfo.InvariantCulture);
                }

                if (area > 0)
                {
                    path +=
                        "&min_area=" +
                        area.ToString(CultureInfo.InvariantCulture);
                }

                using JsonDocument document = GetJson(path);
                List<DirLandReplyData> replies =
                    new List<DirLandReplyData>();

                if (document.RootElement.TryGetProperty(
                        "listings",
                        out JsonElement listings) &&
                    listings.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement listing in listings.EnumerateArray())
                    {
                        if (!TryUuid(listing, "parcel_id", out UUID parcelId))
                            continue;

                        replies.Add(
                            new DirLandReplyData
                            {
                                parcelID = parcelId,
                                name = String(listing, "parcel_name"),
                                auction = false,
                                forSale = true,
                                salePrice = Int32(listing, "price"),
                                actualArea = Int32(listing, "area")
                            });
                    }
                }

                remoteClient.SendDirLandReply(
                    queryId,
                    replies.ToArray());
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-DISCOVERY]: Firestorm Land query failed: {0}",
                    e.Message);
                remoteClient.SendDirLandReply(
                    queryId,
                    Array.Empty<DirLandReplyData>());
            }
        }

        private void OnDirClassifiedQuery(
            IClientAPI remoteClient,
            UUID queryId,
            string queryText,
            uint queryFlags,
            uint category,
            int queryStart)
        {
            try
            {
                string path =
                    "/api/v1/classifieds?q=" +
                    Escape(queryText) +
                    "&offset=" +
                    Math.Max(0, queryStart).ToString(CultureInfo.InvariantCulture) +
                    "&limit=100";

                using JsonDocument document = GetJson(path);
                List<DirClassifiedReplyData> replies =
                    new List<DirClassifiedReplyData>();

                if (document.RootElement.TryGetProperty(
                        "classifieds",
                        out JsonElement classifieds) &&
                    classifieds.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement classified in classifieds.EnumerateArray())
                    {
                        if (!TryUuid(
                                classified,
                                "classified_id",
                                out UUID classifiedId))
                        {
                            continue;
                        }

                        replies.Add(
                            new DirClassifiedReplyData
                            {
                                classifiedID = classifiedId,
                                name = String(classified, "name"),
                                classifiedFlags = 0,
                                creationDate = UnixSeconds(classified, "created_at"),
                                expirationDate = UnixSeconds(classified, "updated_at"),
                                price = 0,
                                Status = 0
                            });
                    }
                }

                remoteClient.SendDirClassifiedReply(
                    queryId,
                    replies.ToArray());
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-DISCOVERY]: Firestorm Classified query failed: {0}",
                    e.Message);
                remoteClient.SendDirClassifiedReply(
                    queryId,
                    Array.Empty<DirClassifiedReplyData>());
            }
        }

        private JsonDocument GetJson(string path)
        {
            using HttpRequestMessage request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    m_WorldApiBaseUrl + path);

            request.Headers.TryAddWithoutValidation(
                "X-NexVerse-Viewer-Agent",
                "Firestorm/OpenSim legacy directory");

            using HttpResponseMessage response =
                m_Http.Send(request);

            response.EnsureSuccessStatusCode();

            string json =
                response.Content
                    .ReadAsStringAsync()
                    .GetAwaiter()
                    .GetResult();

            return JsonDocument.Parse(json);
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

        private static string Escape(string value) =>
            Uri.EscapeDataString(
                (value ?? string.Empty).Trim());

        private static string String(
            JsonElement element,
            string name)
        {
            if (!element.TryGetProperty(
                    name,
                    out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return value.GetString() ?? string.Empty;
        }

        private static int Int32(
            JsonElement element,
            string name)
        {
            if (!element.TryGetProperty(
                    name,
                    out JsonElement value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int intValue))
            {
                return intValue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long longValue))
            {
                return (int)Math.Clamp(
                    longValue,
                    int.MinValue,
                    int.MaxValue);
            }

            return 0;
        }

        private static float Float(
            JsonElement element,
            string name)
        {
            if (element.TryGetProperty(
                    name,
                    out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetSingle(out float result))
            {
                return result;
            }

            return 0f;
        }

        private static bool TryUuid(
            JsonElement element,
            string name,
            out UUID value)
        {
            value = UUID.Zero;

            if (!element.TryGetProperty(
                    name,
                    out JsonElement raw) ||
                raw.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return UUID.TryParse(
                raw.GetString(),
                out value) &&
                !value.IsZero();
        }

        private static uint UnixSeconds(
            JsonElement element,
            string name)
        {
            string raw = String(element, name);
            if (!DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset value))
            {
                return 0;
            }

            long seconds = value.ToUnixTimeSeconds();
            return seconds <= 0
                ? 0
                : seconds >= uint.MaxValue
                    ? uint.MaxValue
                    : (uint)seconds;
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
