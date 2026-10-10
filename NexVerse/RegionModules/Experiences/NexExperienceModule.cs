// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using NexVerse.Core.Experiences;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Experiences
{
    [Extension(
        Path = "/OpenSim/RegionModules",
        NodeName = "RegionModule",
        Id = "NexExperienceModule")]
    public sealed class NexExperienceModule :
        IExperienceModule,
        ISharedRegionModule
    {
        private static readonly ILog m_Log =
            LogManager.GetLogger(typeof(NexExperienceModule));

        private readonly object m_Sync = new object();
        private readonly List<Scene> m_Scenes = new List<Scene>();
        // Per-region listener captures the exact scene whose login CAPS
        // issued the secret. A stale CAP must never become valid again
        // merely because the same avatar is online in ANOTHER region.
        private readonly Dictionary<Scene, EventManager.RegisterCapsEvent>
            m_ViewerCapListeners = new();
        private sealed class ConsentLifecycle
        {
            public EventManager.ScriptResetDelegate Reset;
            public EventManager.RemoveScript RemoveScript;
            public EventManager.OnRemovePresenceDelegate RemovePresence;
            public EventManager.OnMakeChildAgentDelegate MakeChild;
        }
        private readonly Dictionary<Scene, ConsentLifecycle> m_Lifecycle = new();

        private bool m_Enabled;
        private bool m_FirestormReadCaps;
        private bool m_FirestormPermissionCaps;
        private bool m_ScriptPendingConsent;
        private bool m_NativeExperiencePrompt;
        private readonly NexPendingExperienceQueue m_Pending = new();
        private readonly NexExperienceCapLeaseRegistry m_CapLeases = new();
        private Timer m_PendingTimer;
        private string m_ViewerPermissionsApiKey = string.Empty;
        private string m_WorldApiBaseUrl = string.Empty;
        private string m_ApiKey = string.Empty;
        private int m_RequestTimeoutMilliseconds = 3000;
        private HttpClient m_Http;

        public string Name => "NexExperienceModule";
        public Type ReplaceableInterface => typeof(IExperienceModule);

        public void Initialise(IConfigSource config)
        {
            IConfig section =
                config?.Configs["NexExperiencesViewer"];

            if (section == null ||
                !section.GetBoolean("Enabled", false))
            {
                m_Enabled = false;
                return;
            }

            m_WorldApiBaseUrl =
                section.GetString("WorldApiBaseUrl", string.Empty)
                    .Trim()
                    .TrimEnd('/');

            m_ApiKey =
                section.GetString("ApiKey", string.Empty)
                    .Trim();

            m_FirestormReadCaps =
                section.GetBoolean("FirestormReadCaps", false);
            m_FirestormPermissionCaps =
                section.GetBoolean("FirestormPermissionCaps", false);
            m_ScriptPendingConsent =
                section.GetBoolean("ScriptPendingConsent", false);
            m_NativeExperiencePrompt =
                section.GetBoolean("NativeExperiencePrompt", false);
            if (m_NativeExperiencePrompt && !m_ScriptPendingConsent)
                throw new InvalidOperationException(
                    "[NEX-EXPERIENCES]: NativeExperiencePrompt requires ScriptPendingConsent.");
            if (m_ScriptPendingConsent && !m_FirestormPermissionCaps)
                throw new InvalidOperationException(
                    "[NEX-EXPERIENCES]: ScriptPendingConsent requires FirestormPermissionCaps.");
            if (m_FirestormPermissionCaps)
            {
                if (!m_FirestormReadCaps)
                    throw new InvalidOperationException(
                        "[NEX-EXPERIENCES]: FirestormPermissionCaps requires FirestormReadCaps.");
                m_ViewerPermissionsApiKey = section.GetString(
                    "ViewerPermissionsApiKey", string.Empty).Trim();
                if (m_ViewerPermissionsApiKey.Length < 24 ||
                    m_ViewerPermissionsApiKey.StartsWith("${", StringComparison.Ordinal) ||
                    string.Equals(m_ViewerPermissionsApiKey, m_ApiKey,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "[NEX-EXPERIENCES]: A separate experiences:viewer:permissions service API key is required.");
            }

            m_RequestTimeoutMilliseconds =
                Math.Clamp(
                    section.GetInt("RequestTimeoutMilliseconds", 3000),
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
                    "[NEX-EXPERIENCES]: WorldApiBaseUrl must be an absolute HTTP(S) URL.");
            }

            if (string.IsNullOrWhiteSpace(m_ApiKey))
                throw new InvalidOperationException(
                    "[NEX-EXPERIENCES]: ApiKey is required when enabled.");

            m_Http =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromMilliseconds(
                            m_RequestTimeoutMilliseconds)
                };

            m_Enabled = true;

            m_Log.InfoFormat(
                "[NEX-EXPERIENCES]: Central Experience adapter enabled for {0}.",
                m_WorldApiBaseUrl);
        }

        public void PostInitialise()
        {
            if (m_Enabled && m_ScriptPendingConsent)
                m_PendingTimer = new Timer(_ => ExpirePending(),
                    null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;

            lock (m_Sync)
            {
                if (m_Scenes.Contains(scene)) return;
                m_Scenes.Add(scene);
                if (m_FirestormReadCaps)
                {
                    EventManager.RegisterCapsEvent listener =
                        (avatar, caps) => RegisterFirestormReadCaps(
                            scene, avatar, caps);
                    m_ViewerCapListeners[scene] = listener;
                    scene.EventManager.OnRegisterCaps += listener;
                }
                if (m_ScriptPendingConsent || m_FirestormReadCaps)
                {
                    Guid region = scene.RegionInfo.RegionID.Guid;
                    ConsentLifecycle lifecycle = new();
                    lifecycle.Reset = (_, scriptId) =>
                        CancelPendingSilently(m_Pending.CancelScript(region, scriptId.Guid));
                    lifecycle.RemoveScript = (_, scriptId) =>
                        CancelPendingSilently(m_Pending.CancelScript(region, scriptId.Guid));
                    lifecycle.RemovePresence = avatar =>
                    {
                        m_CapLeases.InvalidateResident(region, avatar.Guid);
                        CancelPendingSilently(m_Pending.CancelResident(region, avatar.Guid));
                    };
                    lifecycle.MakeChild = presence =>
                    {
                        if (presence != null)
                        {
                            m_CapLeases.InvalidateResident(region, presence.UUID.Guid);
                            CancelPendingSilently(m_Pending.CancelResident(
                                region, presence.UUID.Guid));
                        }
                    };
                    scene.EventManager.OnScriptReset += lifecycle.Reset;
                    scene.EventManager.OnRemoveScript += lifecycle.RemoveScript;
                    scene.EventManager.OnRemovePresence += lifecycle.RemovePresence;
                    scene.EventManager.OnMakeChildAgent += lifecycle.MakeChild;
                    m_Lifecycle[scene] = lifecycle;
                }
            }
            scene.RegisterModuleInterface<IExperienceModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null)
                return;

            scene.UnregisterModuleInterface<IExperienceModule>(this);
            lock (m_Sync)
            {
                if (m_ViewerCapListeners.Remove(scene,
                        out EventManager.RegisterCapsEvent listener))
                    scene.EventManager.OnRegisterCaps -= listener;
                m_Scenes.Remove(scene);
                if (m_Lifecycle.Remove(scene, out ConsentLifecycle lifecycle))
                    UnsubscribeLifecycle(scene, lifecycle);
            }
            m_CapLeases.InvalidateRegion(scene.RegionInfo.RegionID.Guid);
            CancelPendingSilently(
                m_Pending.CancelRegion(scene.RegionInfo.RegionID.Guid));
        }

        public void Close()
        {
            m_PendingTimer?.Dispose();
            m_PendingTimer = null;
            m_CapLeases.InvalidateAll();
            CancelPendingSilently(m_Pending.CancelAll());
            lock (m_Sync)
            {
                foreach (var listener in m_ViewerCapListeners)
                    listener.Key.EventManager.OnRegisterCaps -= listener.Value;
                m_ViewerCapListeners.Clear();
                foreach (var lifecycle in m_Lifecycle)
                    UnsubscribeLifecycle(lifecycle.Key, lifecycle.Value);
                m_Lifecycle.Clear();
                m_Scenes.Clear();
            }

            m_Http?.Dispose();
            m_Http = null;
        }

        /// <summary>
        /// Firestorm Experience discovery. The CAPS secret is unique to the
        /// viewer session. All remote calls stay on the trusted simulator and
        /// use a server-side API key (never returned to the viewer).
        ///
        /// GetExperienceInfo requires an additional /id/ path, so it uses
        /// the explicitly supported variable-path listener. CAPS owns the
        /// registration and removes it when the viewer session closes.
        /// This is intentionally read-only: the existing Experience consent
        /// flow has not yet been verified and must not be spoofed.
        /// </summary>
        private void RegisterFirestormReadCaps(Scene issuingScene, UUID avatar, Caps caps)
        {
            // Login capability negotiation can precede full avatar arrival.
            // Register the random per-session URLs now; validate the issuing
            // region and live root agent on EVERY actual HTTP request.
            if (!m_Enabled || !m_FirestormReadCaps || caps == null)
                return;
            lock (m_Sync)
                if (!m_Scenes.Contains(issuingScene))
                    return;
            // Every CAPS negotiation gets a fresh bearer lease. If this
            // resident returns to the SAME region, old secret URLs are
            // revoked even if an old HTTP handler still exists.
            NexExperienceCapLeaseRegistry.Lease lease = m_CapLeases.Issue(
                issuingScene.RegionInfo.RegionID.Guid, avatar.Guid);
            var info = new SimpleStreamHandler("/" + UUID.Random(),
                (request, response) => HandleFirestormRead(
                    request, response, issuingScene, avatar, lease, false));
            // Register with CAPS for correct per-session URL generation and
            // destruction, then register its handler as a variable path
            // because Firestorm appends /id/ to GetExperienceInfo.
            caps.RegisterSimpleHandler("GetExperienceInfo", info, false);
            caps.HttpListener.AddSimpleStreamHandler(info, true);
            caps.RegisterSimpleHandler("FindExperienceByName",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (request, response) => HandleFirestormRead(request, response, issuingScene, avatar, lease, true)));
            // Mutating capabilities require an explicitly configured and
            // distinct high-trust simulator key. They are off by default.
            if (m_FirestormPermissionCaps)
            {
                caps.RegisterSimpleHandler("GetExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerPermissions(req, resp, issuingScene, avatar, lease, true)));
                caps.RegisterSimpleHandler("ExperiencePreferences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerPermissions(req, resp, issuingScene, avatar, lease, false)));
                // Firestorm reads these three tabs separately. Each reply
                // contains ONLY the avatar's own role IDs. Creation is
                // deliberately unsupported (GET-only AgentExperiences).
                caps.RegisterSimpleHandler("AgentExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerRoleList(req, resp, issuingScene, avatar, lease, "experience_ids")));
                caps.RegisterSimpleHandler("GetAdminExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerRoleList(req, resp, issuingScene, avatar, lease, "admin_ids")));
                caps.RegisterSimpleHandler("GetCreatorExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerRoleList(req, resp, issuingScene, avatar, lease, "contributor_ids")));
                caps.RegisterSimpleHandler("GroupExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleViewerRoleList(req, resp, issuingScene, avatar, lease, "group")));
            }
        }

        private HttpRequestMessage CreateViewerPermissionRequest(HttpMethod method,
            string path, string json = null)
        {
            HttpRequestMessage outgoing = new(method, m_WorldApiBaseUrl + path);
            outgoing.Headers.TryAddWithoutValidation("X-NexVerse-Api-Key",
                m_ViewerPermissionsApiKey);
            outgoing.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            if (json != null)
                outgoing.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return outgoing;
        }

        private OSDMap FetchViewerPermissionLists(UUID avatar)
        {
            using HttpRequestMessage request = CreateViewerPermissionRequest(HttpMethod.Get,
                "/api/v1/experiences/viewer/permissions?resident_id=" +
                Uri.EscapeDataString(avatar.ToString()));
            using HttpResponseMessage response = m_Http.Send(request);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            OSDMap result = new();
            foreach (string list in new[] { "experiences", "blocked", "experience_ids",
                "admin_ids", "contributor_ids" })
            {
                OSDArray ids = new();
                if (doc.RootElement.TryGetProperty(list, out JsonElement src) &&
                    src.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement id in src.EnumerateArray())
                    {
                        if (id.ValueKind == JsonValueKind.String &&
                            UUID.TryParse(id.GetString(), out UUID uuid) && !uuid.IsZero())
                            ids.Add(OSD.FromUUID(uuid));
                    }
                }
                result[list] = ids;
            }
            return result;
        }

        /// <summary>
        /// Firestorm role tabs require {"experience_ids":[uuid,...]}.
        /// The list is always derived from this CAP's authenticated avatar.
        /// GroupExperiences instead requests public group-owned ids via its
        /// own bridge route; never accesses another resident's role lists.
        /// </summary>
        private void HandleViewerRoleList(IOSHttpRequest req, IOSHttpResponse resp,
            Scene issuingScene, UUID avatar,
            NexExperienceCapLeaseRegistry.Lease lease, string role)
        {
            resp.ContentType = "application/llsd+xml";
            resp.KeepAlive = false;
            resp.AddHeader("Cache-Control", "no-store");
            if (req.HttpMethod != "GET")
            {
                resp.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (!IsCurrentViewer(issuingScene, avatar, lease))
            {
                resp.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }
            try
            {
                OSDArray ids;
                if (role == "group")
                {
                    // Firestorm appends '?' + group UUID, without a key.
                    string query = req.Url?.Query?.TrimStart('?') ?? "";
                    if (query.Length > 36 ||
                        !UUID.TryParse(query, out UUID groupId) || groupId.IsZero())
                    {
                        resp.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                    using HttpRequestMessage request = CreateViewerPermissionRequest(
                        HttpMethod.Get, "/api/v1/experiences/viewer/group?group_id=" +
                        Uri.EscapeDataString(groupId.ToString()));
                    using HttpResponseMessage reply = m_Http.Send(request);
                    reply.EnsureSuccessStatusCode();
                    using JsonDocument document = JsonDocument.Parse(
                        reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    ids = new OSDArray();
                    if (document.RootElement.TryGetProperty("experience_ids",
                            out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in list.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String &&
                                UUID.TryParse(item.GetString(), out UUID id) && !id.IsZero())
                                ids.Add(OSD.FromUUID(id));
                        }
                    }
                }
                else
                {
                    OSDMap resident = FetchViewerPermissionLists(avatar);
                    if (!resident.TryGetValue(role, out OSD value) || value is not OSDArray array)
                    {
                        resp.StatusCode = (int)HttpStatusCode.BadGateway;
                        return;
                    }
                    ids = array;
                }
                OSDMap result = new() { ["experience_ids"] = ids };
                resp.RawBuffer = Encoding.UTF8.GetBytes(
                    OSDParser.SerializeLLSDXmlString(result));
                resp.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception e) when (e is HttpRequestException ||
                                      e is TaskCanceledException ||
                                      e is JsonException ||
                                      e is InvalidOperationException ||
                                      e is ArgumentException)
            {
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Viewer role-list capability failed: {0}",
                    e.Message);
                resp.StatusCode = (int)HttpStatusCode.BadGateway;
            }
        }

        private void HandleViewerPermissions(IOSHttpRequest req, IOSHttpResponse resp,
            Scene issuingScene, UUID avatar,
            NexExperienceCapLeaseRegistry.Lease lease, bool listOnly)
        {
            resp.ContentType = "application/llsd+xml";
            resp.KeepAlive = false;
            resp.AddHeader("Cache-Control", "no-store");
            if (!IsCurrentViewer(issuingScene, avatar, lease))
            {
                resp.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }
            if (listOnly && req.HttpMethod != "GET" ||
                !listOnly && req.HttpMethod != "GET" &&
                req.HttpMethod != "PUT" && req.HttpMethod != "DELETE")
            {
                resp.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            try
            {
                if (!listOnly && req.HttpMethod != "GET")
                {
                    UUID id;
                    string status;
                    if (req.HttpMethod == "DELETE")
                    {
                        string raw = req.Url?.Query?.TrimStart('?') ?? "";
                        if (!UUID.TryParse(Uri.UnescapeDataString(raw), out id) ||
                            id.IsZero())
                        {
                            resp.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                        status = "none";
                    }
                    else
                    {
                        if (req.InputStream == null)
                        {
                            resp.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                        // Bound XML input before parsing to prevent a trivial
                        // large-body allocation on viewer-facing CAPS.
                        using MemoryStream buffer = new();
                        byte[] chunk = new byte[1024];
                        int read;
                        while ((read = req.InputStream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            if (buffer.Length + read > 16384)
                            {
                                resp.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                                return;
                            }
                            buffer.Write(chunk, 0, read);
                        }
                        buffer.Position = 0;
                        if (OSDParser.DeserializeLLSDXml(buffer) is not OSDMap permissions ||
                            permissions.Count != 1)
                        {
                            resp.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                        id = UUID.Zero;
                        status = null;
                        foreach (KeyValuePair<string, OSD> pair in permissions)
                        {
                            if (!UUID.TryParse(pair.Key, out id) || id.IsZero() ||
                                pair.Value is not OSDMap entry ||
                                !entry.TryGetValue("permission", out OSD rawPermission))
                            {
                                resp.StatusCode = (int)HttpStatusCode.BadRequest;
                                return;
                            }
                            status = rawPermission.AsString() switch
                            {
                                "Allow" => "allowed",
                                "Block" => "blocked",
                                _ => null
                            };
                        }
                        if (status == null)
                        {
                            resp.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                    }

                    // ONLY the authenticated CAP's live avatar is forwarded.
                    // Never forward a resident_id from client XML/query data.
                    string json = JsonSerializer.Serialize(new
                    {
                        resident_id = avatar.ToString(),
                        experience_id = id.ToString(),
                        status
                    });
                    using HttpRequestMessage change = CreateViewerPermissionRequest(
                        HttpMethod.Put, "/api/v1/experiences/viewer/permissions", json);
                    using HttpResponseMessage changed = m_Http.Send(change);
                    if (!changed.IsSuccessStatusCode)
                    {
                        resp.StatusCode = (int)changed.StatusCode;
                        return;
                    }
                    // Only the successfully persisted viewer-side decision
                    // may settle script requests. A re-check of binding and
                    // parcel policy follows before signaling success.
                    if (m_ScriptPendingConsent)
                        ResolveViewerPendingConsent(avatar, id,
                            status == "allowed");
                }
                OSDMap lists = FetchViewerPermissionLists(avatar);
                resp.RawBuffer = Encoding.UTF8.GetBytes(
                    OSDParser.SerializeLLSDXmlString(lists));
                resp.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception e) when (e is HttpRequestException ||
                                      e is TaskCanceledException ||
                                      e is JsonException ||
                                      e is InvalidOperationException ||
                                      e is System.Xml.XmlException ||
                                      e is IOException ||
                                      e is ArgumentException)
            {
                m_Log.WarnFormat("[NEX-EXPERIENCES]: Viewer permissions capability failed: {0}",
                    e.Message);
                resp.StatusCode = (int)HttpStatusCode.BadGateway;
            }
        }

        private bool IsCurrentViewer(Scene issuingScene, UUID avatar,
            NexExperienceCapLeaseRegistry.Lease lease)
        {
            if (issuingScene == null || avatar.IsZero() ||
                !m_CapLeases.IsCurrent(lease) ||
                lease.Region != issuingScene.RegionInfo.RegionID.Guid ||
                lease.Resident != avatar.Guid)
                return false;
            lock (m_Sync)
            {
                if (!m_Scenes.Contains(issuingScene))
                    return false;
            }
            return issuingScene.TryGetScenePresence(avatar,
                out ScenePresence presence) &&
                presence != null && !presence.IsDeleted &&
                !presence.IsNPC && !presence.IsChildAgent &&
                presence.ControllingClient != null;
        }

        private static OSDMap ViewerExperience(JsonElement e)
        {
            string id = e.GetProperty("experience_id").GetString();
            string owner = e.GetProperty("owner_id").GetString();
            string group = e.GetProperty("group_id").GetString();
            bool enabled = e.GetProperty("enabled").GetBoolean();
            UUID.TryParse(id, out UUID publicId);
            UUID.TryParse(owner, out UUID ownerId);
            UUID.TryParse(group, out UUID groupId);
            return new OSDMap
            {
                ["public_id"] = OSD.FromUUID(publicId),
                ["agent_id"] = OSD.FromUUID(ownerId),
                ["group_id"] = OSD.FromUUID(groupId),
                ["name"] = OSD.FromString(e.GetProperty("name").GetString() ?? ""),
                ["description"] = OSD.FromString(e.GetProperty("description").GetString() ?? ""),
                ["maturity"] = OSD.FromInteger(e.GetProperty("maturity").GetInt32()),
                ["properties"] = OSD.FromInteger(enabled ? 0 : 1 << 6),
                ["expiration"] = OSD.FromReal(600.0),
                ["quota"] = OSD.FromInteger(128)
            };
        }

        private void HandleFirestormRead(IOSHttpRequest request,
            IOSHttpResponse response, Scene issuingScene, UUID avatar,
            NexExperienceCapLeaseRegistry.Lease lease, bool search)
        {
            response.ContentType = "application/llsd+xml";
            response.KeepAlive = false;
            response.AddHeader("Cache-Control", "no-store");
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (!IsCurrentViewer(issuingScene, avatar, lease))
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }
            try
            {
                string apiPath;
                if (search)
                {
                    string query = request.QueryString?["query"] ?? "";
                    if (query.Length > 160)
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                    int page = int.TryParse(request.QueryString?["page"], out int p)
                        ? Math.Clamp(p, 0, 1000) : 0;
                    int size = int.TryParse(request.QueryString?["page_size"], out int count)
                        ? Math.Clamp(count, 1, 50) : 20;
                    apiPath = "/api/v1/experiences/script/search?q=" +
                        Uri.EscapeDataString(query) + "&offset=" + (page * size) +
                        "&limit=" + size;
                }
                else
                {
                    string[] ids = request.QueryString?.GetValues("public_id") ??
                        Array.Empty<string>();
                    if (ids.Length == 0 || ids.Length > 64)
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                    foreach (string raw in ids)
                    {
                        if (!UUID.TryParse(raw, out UUID id) || id.IsZero())
                        {
                            response.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                    }
                    apiPath = "/api/v1/experiences/script/info?ids=" +
                        Uri.EscapeDataString(string.Join(",", ids));
                }

                using HttpRequestMessage upstreamRequest = CreateRequest(HttpMethod.Get, apiPath);
                using HttpResponseMessage upstream = m_Http.Send(upstreamRequest);
                if (!upstream.IsSuccessStatusCode)
                {
                    response.StatusCode = (int)HttpStatusCode.BadGateway;
                    return;
                }

                using JsonDocument body = JsonDocument.Parse(
                    upstream.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                OSDArray entries = new();
                if (body.RootElement.TryGetProperty("experiences", out JsonElement items))
                {
                    foreach (JsonElement e in items.EnumerateArray())
                        entries.Add(ViewerExperience(e));
                }
                OSDMap result = new() { ["experience_keys"] = entries };
                if (!search)
                {
                    OSDArray errors = new();
                    if (body.RootElement.TryGetProperty("error_ids", out JsonElement missing))
                    {
                        foreach (JsonElement id in missing.EnumerateArray())
                        {
                            if (UUID.TryParse(id.GetString(), out UUID uuid))
                                errors.Add(OSD.FromUUID(uuid));
                        }
                    }
                    result["error_ids"] = errors;
                }
                response.RawBuffer = Encoding.UTF8.GetBytes(
                    OSDParser.SerializeLLSDXmlString(result));
                response.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception e) when (e is HttpRequestException ||
                                      e is JsonException || e is TaskCanceledException ||
                                      e is InvalidOperationException ||
                                      e is ArgumentException)
            {
                m_Log.WarnFormat("[NEX-EXPERIENCES]: Firestorm readonly CAP failed: {0}", e.Message);
                response.StatusCode = (int)HttpStatusCode.BadGateway;
            }
        }

        public UUID ResolveExperience(UUID scriptItemId)
        {
            if (!m_Enabled || scriptItemId.IsZero())
                return UUID.Zero;

            try
            {
                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/resolve?script_id=" +
                        Uri.EscapeDataString(scriptItemId.ToString()));

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (!response.IsSuccessStatusCode)
                    return UUID.Zero;

                using JsonDocument document =
                    JsonDocument.Parse(
                        response.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                if (!document.RootElement.TryGetProperty(
                        "experience_id",
                        out JsonElement value) ||
                    value.ValueKind != JsonValueKind.String ||
                    !UUID.TryParse(value.GetString(), out UUID experienceId))
                {
                    return UUID.Zero;
                }

                return experienceId;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Resolve for script {0} failed: {1}",
                    scriptItemId,
                    e.Message);
                return UUID.Zero;
            }
        }

        public bool TryGetExperienceDetails(
            UUID experienceId,
            out string name,
            out UUID ownerId,
            out UUID groupId,
            out int maturity,
            out bool enabled)
        {
            name = string.Empty;
            ownerId = UUID.Zero;
            groupId = UUID.Zero;
            maturity = 0;
            enabled = false;

            if (!m_Enabled || experienceId.IsZero())
                return false;

            try
            {
                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/details?experience_id=" +
                        Uri.EscapeDataString(
                            experienceId.ToString()));

                using HttpResponseMessage response =
                    m_Http.Send(request);

                if (!response.IsSuccessStatusCode)
                    return false;

                using JsonDocument document =
                    JsonDocument.Parse(
                        response.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                if (!document.RootElement.TryGetProperty(
                        "experience",
                        out JsonElement experience))
                {
                    return false;
                }

                name =
                    experience.TryGetProperty("name", out JsonElement nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                if (experience.TryGetProperty("owner_id", out JsonElement ownerElement) &&
                    ownerElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(ownerElement.GetString(), out ownerId);
                }

                if (experience.TryGetProperty("group_id", out JsonElement groupElement) &&
                    groupElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(groupElement.GetString(), out groupId);
                }

                string maturityName =
                    experience.TryGetProperty("maturity", out JsonElement maturityElement) &&
                    maturityElement.ValueKind == JsonValueKind.String
                        ? maturityElement.GetString() ?? "general"
                        : "general";

                maturity =
                    maturityName.Equals("adult", StringComparison.OrdinalIgnoreCase)
                        ? 2
                        : maturityName.Equals("moderate", StringComparison.OrdinalIgnoreCase)
                            ? 1
                            : 0;

                enabled =
                    experience.TryGetProperty("enabled", out JsonElement enabledElement) &&
                    enabledElement.ValueKind == JsonValueKind.True;

                return true;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Details for {0} failed: {1}",
                    experienceId,
                    e.Message);
                return false;
            }
        }

        private static void UnsubscribeLifecycle(
            Scene scene, ConsentLifecycle lifecycle)
        {
            scene.EventManager.OnScriptReset -= lifecycle.Reset;
            scene.EventManager.OnRemoveScript -= lifecycle.RemoveScript;
            scene.EventManager.OnRemovePresence -= lifecycle.RemovePresence;
            scene.EventManager.OnMakeChildAgent -= lifecycle.MakeChild;
        }

        private static void CancelPendingSilently(
            NexPendingExperienceRequest[] requests)
        {
            // No stale LSL events after script reset, removal, region
            // departure or simulator shutdown. Only native callbacks detach.
            foreach (NexPendingExperienceRequest request in requests)
            {
                try { request.Cancel?.Invoke(); }
                catch (Exception e)
                {
                    m_Log.WarnFormat(
                        "[NEX-EXPERIENCES]: Pending consent cleanup failed: {0}",
                        e.Message);
                }
            }
        }

        private static void CompleteRequests(
            NexPendingExperienceRequest[] requests, int result)
        {
            foreach (NexPendingExperienceRequest request in requests)
            {
                try { request.Completion(result); }
                catch (Exception e)
                {
                    m_Log.WarnFormat(
                        "[NEX-EXPERIENCES]: Failed to deliver pending LSL consent event: {0}",
                        e.Message);
                }
            }
        }

        private void ExpirePending()
        {
            try { CompleteRequests(m_Pending.Expire(DateTimeOffset.UtcNow), 18); }
            catch (Exception e)
            {
                m_Log.WarnFormat("[NEX-EXPERIENCES]: Pending consent cleanup failed: {0}",
                    e.Message);
            }
        }

        private bool FindLiveConsentContext(UUID residentId, UUID objectId,
            UUID scriptItemId, out Scene scene, out ScenePresence presence)
        {
            scene = null;
            presence = null;
            Scene[] scenes;
            lock (m_Sync) scenes = m_Scenes.ToArray();
            foreach (Scene current in scenes)
            {
                SceneObjectPart part = current.GetSceneObjectPart(objectId);
                if (part == null || part.ParentGroup == null ||
                    part.ParentGroup.IsDeleted || part.TaskInventory == null ||
                    part.Inventory.GetInventoryItem(scriptItemId) == null)
                    continue;
                if (!current.TryGetScenePresence(residentId, out ScenePresence agent) ||
                    agent == null || agent.IsDeleted || agent.IsNPC ||
                    agent.IsChildAgent || agent.ControllingClient == null)
                    continue;
                scene = current;
                presence = agent;
                return true;
            }
            return false;
        }

        public bool QueueExperiencePermissionRequest(UUID scriptItemId, UUID objectId,
            UUID residentId, UUID parcelId, Action<int> onResult)
        {
            if (!m_Enabled || !m_ScriptPendingConsent || onResult == null ||
                scriptItemId.IsZero() || objectId.IsZero() || residentId.IsZero() ||
                !FindLiveConsentContext(residentId, objectId, scriptItemId,
                    out Scene scene, out ScenePresence presence))
                return false;

            try
            {
                // Do not solicit any consent for an Experience that the
                // resident has actively BLOCKED or that is not script-bound.
                using HttpRequestMessage check = CreateRequest(HttpMethod.Get,
                    "/api/v1/experiences/script/permission?script_id=" +
                    Uri.EscapeDataString(scriptItemId.ToString()) + "&resident_id=" +
                    Uri.EscapeDataString(residentId.ToString()));
                using HttpResponseMessage response = m_Http.Send(check);
                if (!response.IsSuccessStatusCode)
                    return false;
                using JsonDocument doc = JsonDocument.Parse(
                    response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                JsonElement body = doc.RootElement;
                if (!body.TryGetProperty("status", out JsonElement status) ||
                    status.GetString() != "none" ||
                    !body.TryGetProperty("experience_id", out JsonElement id) ||
                    !UUID.TryParse(id.GetString(), out UUID experienceId) ||
                    experienceId.IsZero())
                    return false;
                if (!TryGetExperienceDetails(experienceId, out _, out _, out _,
                        out _, out bool experienceEnabled) || !experienceEnabled)
                    return false;

                // An Experience blocked at the parcel/estate layer must
                // never be queued merely because its resident has no grant.
                if (!parcelId.IsZero())
                {
                    using HttpRequestMessage loc = CreateRequest(HttpMethod.Get,
                        "/api/v1/experiences/script/location?script_id=" +
                        Uri.EscapeDataString(scriptItemId.ToString()) + "&parcel_id=" +
                        Uri.EscapeDataString(parcelId.ToString()));
                    using HttpResponseMessage locResponse = m_Http.Send(loc);
                    if (!locResponse.IsSuccessStatusCode)
                        return false;
                    using JsonDocument locDocument = JsonDocument.Parse(
                        locResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    if (!locDocument.RootElement.TryGetProperty("allowed",
                            out JsonElement grant) ||
                        grant.ValueKind != JsonValueKind.True)
                        return false;
                }

                IClientAPI consentClient = presence.ControllingClient;
                // ScriptAnswerYes with zero questions is the resident's
                // immediate "Deny" response to the native dialog. Firestorm
                // sends the "Allow" decision separately through the authenticated
                // ExperiencePreferences capability. Never convert a nonzero
                // ScriptAnswerYes mask into a persistent grant.
                ScriptAnswer nativeAnswerHandler = null;
                NexPendingExperienceRequest pending = new()
                {
                    ExperienceId = experienceId.Guid,
                    ResidentId = residentId.Guid,
                    RegionId = scene.RegionInfo.RegionID.Guid,
                    ObjectId = objectId.Guid,
                    ScriptId = scriptItemId.Guid,
                    ParcelId = parcelId.Guid,
                    Deadline = DateTimeOffset.UtcNow.AddSeconds(60),
                    Completion = result =>
                    {
                        if (nativeAnswerHandler != null)
                            consentClient.OnScriptAnswer -= nativeAnswerHandler;
                        onResult(result);
                    },
                    Cancel = () =>
                    {
                        if (nativeAnswerHandler != null)
                            consentClient.OnScriptAnswer -= nativeAnswerHandler;
                    }
                };
                if (!m_Pending.TryAdd(pending, DateTimeOffset.UtcNow))
                    return false;

                // Native Firestorm/SL uses ScriptQuestion with the optional
                // Experience block AND a nonzero standard-permissions mask.
                // The zero-bit ordinary ScriptQuestion is not a valid native
                // Experience prompt. Never treat ScriptAnswerYes as a grant:
                // only the dedicated authenticated ExperiencePreferences CAP
                // can persist Allow/Block and resolve this pending LSL event.
                bool nativePromptSent = false;
                if (m_NativeExperiencePrompt &&
                    consentClient is IExperienceQuestionClient client)
                {
                    nativeAnswerHandler = (source, task, item, answer) =>
                    {
                        if (source != consentClient || task != objectId ||
                            item != scriptItemId || answer != 0)
                            return;
                        CompleteRequests(m_Pending.TakeSpecific(
                            residentId.Guid, experienceId.Guid,
                            objectId.Guid, scriptItemId.Guid), 4);
                    };
                    consentClient.OnScriptAnswer += nativeAnswerHandler;
                    try
                    {
                        SceneObjectPart part = scene.GetSceneObjectPart(objectId);
                        if (part != null && part.ParentGroup != null &&
                            !part.ParentGroup.IsDeleted)
                        {
                            string objectName = part.ParentGroup.RootPart.Name;
                            string ownerName = part.OwnerID.ToString();
                            var owner = scene.UserAccountService?.GetUserAccount(
                                scene.RegionInfo.ScopeID, part.OwnerID);
                            if (owner != null)
                                ownerName = owner.FirstName + " " + owner.LastName;
                            // Firestorm's llscriptruntimeperms.h defines
                            // JoinAnExperience as (0x1 << 13). Asking for
                            // unrelated controls/attach/camera/TP permissions
                            // would be misleading and excessively broad.
                            // The viewer recognizes ExperienceID + this bit
                            // and opens ScriptQuestionExperience.
                            const int experiencePermissions = 0x2000;
                            nativePromptSent = client.SendExperienceQuestion(
                                objectId, objectName, ownerName, scriptItemId,
                                experienceId, experiencePermissions);
                        }
                    }
                    catch (Exception e)
                    {
                        m_Log.WarnFormat(
                            "[NEX-EXPERIENCES]: Native Experience prompt unavailable: {0}",
                            e.Message);
                    }
                }

                if (!nativePromptSent)
                {
                    if (nativeAnswerHandler != null)
                    {
                        consentClient.OnScriptAnswer -= nativeAnswerHandler;
                        nativeAnswerHandler = null;
                    }
                    // Fallback is explicitly informational, not an implicit
                    // grant. It also supports non-Firestorm client transports.
                    try
                    {
                        presence.ControllingClient.SendAgentAlertMessage(
                            "Experience permission requested. Open Experiences " +
                            "settings and explicitly Allow or Block it within " +
                            "60 seconds. No permission is granted automatically.",
                            false);
                    }
                    catch (Exception e)
                    {
                        m_Log.WarnFormat(
                            "[NEX-EXPERIENCES]: Pending consent notice failed: {0}",
                            e.Message);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                m_Log.WarnFormat("[NEX-EXPERIENCES]: Consent queue denied safely: {0}",
                    e.Message);
                return false;
            }
        }

        private void ResolveViewerPendingConsent(UUID residentId,
            UUID experienceId, bool allowed)
        {
            NexPendingExperienceRequest[] requests =
                m_Pending.Take(residentId.Guid, experienceId.Guid);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (NexPendingExperienceRequest pending in requests)
            {
                int status = 4; // XP_ERROR_NOT_PERMITTED
                if (pending.Deadline <= now)
                    status = 18; // XP_ERROR_REQUEST_PERM_TIMEOUT
                else if (allowed &&
                    FindLiveConsentContext(new UUID(pending.ResidentId),
                        new UUID(pending.ObjectId), new UUID(pending.ScriptId),
                        out Scene scene, out _) &&
                    scene.RegionInfo.RegionID.Guid == pending.RegionId &&
                    HasExperiencePermission(
                        new UUID(pending.ScriptId),
                        residentId,
                        new UUID(pending.ParcelId),
                        out UUID freshId, out _) &&
                    freshId == experienceId)
                    status = 0;
                CompleteRequests(new[] { pending }, status);
            }
        }

        public bool HasExperiencePermission(
            UUID scriptItemId,
            UUID residentId,
            UUID parcelId,
            out UUID experienceId,
            out string reason)
        {
            experienceId = UUID.Zero;
            reason = string.Empty;

            if (!m_Enabled)
            {
                reason = "Experience service is disabled.";
                return false;
            }

            if (scriptItemId.IsZero() || residentId.IsZero())
            {
                reason = "Script and resident UUID are required.";
                return false;
            }

            try
            {
                using HttpRequestMessage permissionRequest =
                    CreateRequest(
                        HttpMethod.Get,
                        "/api/v1/experiences/script/permission?script_id=" +
                        Uri.EscapeDataString(scriptItemId.ToString()) +
                        "&resident_id=" +
                        Uri.EscapeDataString(residentId.ToString()));

                using HttpResponseMessage permissionResponse =
                    m_Http.Send(permissionRequest);

                if (!permissionResponse.IsSuccessStatusCode)
                {
                    reason = ReadErrorMessage(permissionResponse);
                    return false;
                }

                using JsonDocument permissionDocument =
                    JsonDocument.Parse(
                        permissionResponse.Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult());

                JsonElement root =
                    permissionDocument.RootElement;

                if (root.TryGetProperty("experience_id", out JsonElement experienceElement) &&
                    experienceElement.ValueKind == JsonValueKind.String)
                {
                    UUID.TryParse(
                        experienceElement.GetString(),
                        out experienceId);
                }

                bool residentAllowed =
                    root.TryGetProperty("allowed", out JsonElement allowedElement) &&
                    allowedElement.ValueKind == JsonValueKind.True;

                if (!residentAllowed)
                {
                    reason = "Resident has not granted this experience.";
                    return false;
                }

                if (!parcelId.IsZero())
                {
                    using HttpRequestMessage locationRequest =
                        CreateRequest(
                            HttpMethod.Get,
                            "/api/v1/experiences/script/location?script_id=" +
                            Uri.EscapeDataString(scriptItemId.ToString()) +
                            "&parcel_id=" +
                            Uri.EscapeDataString(parcelId.ToString()));

                    using HttpResponseMessage locationResponse =
                        m_Http.Send(locationRequest);

                    if (!locationResponse.IsSuccessStatusCode)
                    {
                        reason = ReadErrorMessage(locationResponse);
                        return false;
                    }

                    using JsonDocument locationDocument =
                        JsonDocument.Parse(
                            locationResponse.Content
                                .ReadAsStringAsync()
                                .GetAwaiter()
                                .GetResult());

                    bool locationAllowed =
                        locationDocument.RootElement.TryGetProperty(
                            "allowed",
                            out JsonElement locationAllowedElement) &&
                        locationAllowedElement.ValueKind == JsonValueKind.True;

                    if (!locationAllowed)
                    {
                        reason = "Experience is blocked by parcel/estate policy.";
                        return false;
                    }
                }

                return !experienceId.IsZero();
            }
            catch (Exception e)
            {
                reason = "Experience permission check failed.";
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: Permission for script {0}/resident {1} failed: {2}",
                    scriptItemId,
                    residentId,
                    e.Message);
                return false;
            }
        }

        public bool AgentInExperience(
            UUID scriptItemId,
            UUID residentId) =>
            HasExperiencePermission(
                scriptItemId,
                residentId,
                UUID.Zero,
                out _,
                out _);

        public bool CreateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            out string error) =>
            KeyValue(
                scriptItemId,
                "create",
                key,
                value,
                false,
                string.Empty,
                0,
                0,
                out _,
                out _,
                out error);

        public bool ReadKeyValue(
            UUID scriptItemId,
            string key,
            out string value,
            out string error) =>
            KeyValue(
                scriptItemId,
                "read",
                key,
                string.Empty,
                false,
                string.Empty,
                0,
                0,
                out value,
                out _,
                out error);

        public bool UpdateKeyValue(
            UUID scriptItemId,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            out bool retryMismatch,
            out string error)
        {
            retryMismatch = false;

            bool success =
                KeyValue(
                    scriptItemId,
                    "update",
                    key,
                    value,
                    checkOriginal,
                    originalValue,
                    0,
                    0,
                    out _,
                    out string code,
                    out error);

            retryMismatch =
                string.Equals(
                    code,
                    "retry_update",
                    StringComparison.OrdinalIgnoreCase);

            return success;
        }

        public bool DeleteKeyValue(
            UUID scriptItemId,
            string key,
            out string error) =>
            KeyValue(
                scriptItemId,
                "delete",
                key,
                string.Empty,
                false,
                string.Empty,
                0,
                0,
                out _,
                out _,
                out error);

        public bool GetKeyValueStats(
            UUID scriptItemId,
            out long usedBytes,
            out long quotaBytes,
            out int keyCount,
            out string error)
        {
            usedBytes = 0;
            quotaBytes = 0;
            keyCount = 0;

            if (!KeyValue(
                    scriptItemId,
                    "stats",
                    string.Empty,
                    string.Empty,
                    false,
                    string.Empty,
                    0,
                    0,
                    out string result,
                    out _,
                    out error))
            {
                return false;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(result);

                JsonElement root = document.RootElement;
                usedBytes =
                    root.TryGetProperty("used_bytes", out JsonElement used) &&
                    used.TryGetInt64(out long usedValue)
                        ? usedValue
                        : 0L;
                quotaBytes =
                    root.TryGetProperty("quota_bytes", out JsonElement quota) &&
                    quota.TryGetInt64(out long quotaValue)
                        ? quotaValue
                        : 0L;
                keyCount =
                    root.TryGetProperty("key_count", out JsonElement count) &&
                    count.TryGetInt32(out int countValue)
                        ? countValue
                        : 0;

                return true;
            }
            catch
            {
                error = "Invalid Experience K/V stats response.";
                return false;
            }
        }

        public bool ListKeyValueKeys(
            UUID scriptItemId,
            int start,
            int count,
            out string[] keys,
            out string error)
        {
            keys = Array.Empty<string>();

            if (!KeyValue(
                    scriptItemId,
                    "keys",
                    string.Empty,
                    string.Empty,
                    false,
                    string.Empty,
                    start,
                    count,
                    out string result,
                    out _,
                    out error))
            {
                return false;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(result);

                if (!document.RootElement.TryGetProperty(
                        "keys",
                        out JsonElement array) ||
                    array.ValueKind != JsonValueKind.Array)
                {
                    error = "Invalid Experience K/V keys response.";
                    return false;
                }

                List<string> output =
                    new List<string>();

                foreach (JsonElement element in array.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        output.Add(element.GetString() ?? string.Empty);
                }

                keys = output.ToArray();
                return true;
            }
            catch
            {
                error = "Invalid Experience K/V keys response.";
                return false;
            }
        }

        private bool KeyValue(
            UUID scriptItemId,
            string operation,
            string key,
            string value,
            bool checkOriginal,
            string originalValue,
            int start,
            int count,
            out string result,
            out string code,
            out string error)
        {
            result = string.Empty;
            code = string.Empty;
            error = string.Empty;

            if (!m_Enabled || scriptItemId.IsZero())
            {
                error = "Experience service is unavailable.";
                return false;
            }

            try
            {
                byte[] payload =
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            script_id = scriptItemId.ToString(),
                            operation,
                            key,
                            value = value ?? string.Empty,
                            check_original = checkOriginal,
                            original_value = originalValue ?? string.Empty,
                            start,
                            count
                        });

                using HttpRequestMessage request =
                    CreateRequest(
                        HttpMethod.Post,
                        "/api/v1/experiences/script/kv");

                request.Content =
                    new ByteArrayContent(payload);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response =
                    m_Http.Send(request);

                string body =
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    error = ReadErrorMessage(response, body);
                    return false;
                }

                using JsonDocument document =
                    JsonDocument.Parse(body);

                bool success =
                    document.RootElement.TryGetProperty(
                        "success",
                        out JsonElement successElement) &&
                    successElement.ValueKind == JsonValueKind.True;

                if (document.RootElement.TryGetProperty(
                        "result",
                        out JsonElement resultElement))
                {
                    result =
                        resultElement.ValueKind == JsonValueKind.String
                            ? resultElement.GetString() ?? string.Empty
                            : resultElement.GetRawText();
                }

                if (document.RootElement.TryGetProperty(
                        "code",
                        out JsonElement codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String)
                {
                    code =
                        codeElement.GetString() ??
                        string.Empty;
                }

                if (!success)
                {
                    error =
                        document.RootElement.TryGetProperty(
                            "message",
                            out JsonElement messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString() ?? "Experience key/value operation was rejected."
                            : "Experience key/value operation was rejected.";
                }

                return success;
            }
            catch (Exception e)
            {
                error = "Experience key/value service unavailable.";
                m_Log.WarnFormat(
                    "[NEX-EXPERIENCES]: K/V {0} for script {1} failed: {2}",
                    operation,
                    scriptItemId,
                    e.Message);
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(
            HttpMethod method,
            string path)
        {
            HttpRequestMessage request =
                new HttpRequestMessage(
                    method,
                    m_WorldApiBaseUrl +
                    path);

            request.Headers.TryAddWithoutValidation(
                "X-NexVerse-Api-Key",
                m_ApiKey);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            return request;
        }

        private static string ReadErrorMessage(
            HttpResponseMessage response,
            string body = null)
        {
            try
            {
                string json =
                    body ??
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty(
                        "message",
                        out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return
                        message.GetString() ??
                        "Experience request failed.";
                }
            }
            catch
            {
            }

            return
                "Experience request failed (" +
                (int)response.StatusCode +
                ").";
        }
    }
}
