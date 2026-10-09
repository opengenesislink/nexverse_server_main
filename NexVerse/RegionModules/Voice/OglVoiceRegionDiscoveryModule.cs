// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using NexVerse.Core.Voice;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace NexVerse.RegionModules.Voice
{
    // An independent provider discovery module: it does not claim viewer CAPS
    // support, spawn LiveKit participants or provision paid provider accounts.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglVoiceRegionDiscoveryModule")]
    public sealed class OglVoiceRegionDiscoveryModule :
        ISharedRegionModule, IOglVoiceProviderLookup, IOglVoiceSessionAdmission
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(OglVoiceRegionDiscoveryModule));
        private readonly ConcurrentDictionary<UUID, Scene> m_Scenes = new();
        private HttpClient m_Http;
        private Timer m_Timer;
        private Uri m_Authority;
        private string m_Key = string.Empty;
        private string m_Node = string.Empty;
        private bool m_Enabled;
        private bool m_GridManaged;
        private int m_Refreshing;
        private int m_Disposed;
        private OglVoiceProviderDescriptor m_Current;
        private DateTimeOffset m_LastRefresh;

        public string Name => "OGLVoice Provider Discovery";
        public Type ReplaceableInterface => null;
        public bool DiscoveryEnabled => m_Enabled;
        public DateTimeOffset LastRefreshUtc => m_LastRefresh;
        public OglVoiceProviderDescriptor CurrentProvider =>
            m_Enabled &&
            (!m_GridManaged || (DateTimeOffset.UtcNow - m_LastRefresh) < TimeSpan.FromMinutes(10))
                ? Volatile.Read(ref m_Current) : null;

        public void Initialise(IConfigSource source)
        {
            IConfig local = source.Configs["OGLVoice"];
            if (local != null && !local.GetBoolean("Enabled", false))
                return;

            string mode = local?.GetString("Mode", "GridManaged")?.Trim() ?? "GridManaged";
            if (mode.Equals("Standalone", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    OglVoiceProviderDescriptor standalone = new()
                    {
                        provider_url = local.GetString("ServiceUrl", string.Empty).Trim(),
                        tenant_id = local.GetString("TenantId", string.Empty).Trim(),
                        hypergrid_guests = local.GetBoolean("IncludeHypergridGuests", true)
                    };
                    standalone.Validate();
                    m_Current = standalone;
                    m_LastRefresh = DateTimeOffset.UtcNow;
                    m_Enabled = true;
                    m_Log.Info("[OGL-VOICE]: Eigenstaendiger Provider konfiguriert; Viewer-Voice noch nicht aktiv.");
                }
                catch (Exception e)
                {
                    m_Log.Error("[OGL-VOICE]: Standalone-Konfiguration ungueltig; Voice-Discovery deaktiviert.", e);
                }
                return;
            }

            if (!mode.Equals("GridManaged", StringComparison.OrdinalIgnoreCase))
            {
                m_Log.Warn("[OGL-VOICE]: Unbekannter Discovery-Modus; deaktiviert.");
                return;
            }

            // Grid auto-discovery is opt-in through the trusted existing
            // NodeAgent/NexBus relationship, NOT by reading public grid_info.
            IConfig agent = source.Configs["NexVerseNodeAgent"];
            if (agent == null || !agent.GetBoolean("Enabled", false))
                return;

            string peer = agent.GetString("PeerUrl", "").Trim();
            string authorityOverride = local?.GetString("AuthorityUrl", "")?.Trim() ?? "";
            try
            {
                Uri peerUri = new(peer, UriKind.Absolute);
                if (!OglVoiceDiscoveryProof.IsSafeServiceUri(peer))
                    throw new ArgumentException("NodeAgent peer must be HTTPS or loopback HTTP");
                // Default derives the authority's trusted origin from NodeAgent;
                // an explicitly configured path/host is allowed only with TLS.
                string authority = string.IsNullOrEmpty(authorityOverride)
                    ? peerUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') +
                        "/internal/oglvoice/v1/provider"
                    : authorityOverride;
                if (!OglVoiceDiscoveryProof.IsSafeServiceUri(authority))
                    throw new ArgumentException("Authority must be HTTPS or loopback HTTP");

                string key = agent.GetString("SharedKey", "");
                OglVoiceDiscoveryProof.ValidateKey(key);
                string node = agent.GetString("NodeId", Environment.MachineName).Trim();
                // Validate expected NodeId characters before the first remote call.
                OglVoiceDiscoveryProof.RequestSignature(key, node,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), OglVoiceDiscoveryProof.NewNonce());

                m_Authority = new Uri(authority, UriKind.Absolute);
                m_Key = key;
                m_Node = node;
                m_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                m_GridManaged = true;
                m_Enabled = true;
                m_Log.Info("[OGL-VOICE]: Zentraler Robust-Provider wird via NexBus-Vertrauensbeziehung ermittelt.");
            }
            catch (Exception e)
            {
                m_Log.Warn("[OGL-VOICE]: Trusted discovery konnte nicht initialisiert werden: " + e.Message);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;
            m_Scenes[scene.RegionInfo.RegionID] = scene;
            scene.RegisterModuleInterface<IOglVoiceProviderLookup>(this);
            scene.RegisterModuleInterface<IOglVoiceSessionAdmission>(this);
        }

        public bool TryBuildAdmission(UUID agentId, out OglVoiceAdmission assertion)
        {
            assertion = null;
            OglVoiceProviderDescriptor provider = CurrentProvider;
            if (provider == null)
                return false;
            foreach (Scene scene in m_Scenes.Values)
            {
                if (OglVoiceRegionAdmission.TryCreate(scene, agentId,
                    provider.tenant_id, provider.hypergrid_guests, out assertion))
                    return true;
            }
            return false;
        }

        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (scene == null || !m_Scenes.TryRemove(scene.RegionInfo.RegionID, out _))
                return;
            scene.UnregisterModuleInterface<IOglVoiceSessionAdmission>(this);
            scene.UnregisterModuleInterface<IOglVoiceProviderLookup>(this);
        }

        public void PostInitialise()
        {
            if (!m_GridManaged || !m_Enabled)
                return;
            // No synchronous HTTP during simulator startup / region admission.
            m_Timer = new Timer(_ => _ = RefreshAsync(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        }

        private async Task RefreshAsync()
        {
            if (Volatile.Read(ref m_Disposed) != 0 ||
                Interlocked.Exchange(ref m_Refreshing, 1) != 0)
                return;

            try
            {
                string nonce = OglVoiceDiscoveryProof.NewNonce();
                long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string signature = OglVoiceDiscoveryProof.RequestSignature(m_Key, m_Node, stamp, nonce);

                using HttpRequestMessage request = new(HttpMethod.Get, m_Authority);
                request.Headers.TryAddWithoutValidation("X-OGLVoice-Node", m_Node);
                request.Headers.TryAddWithoutValidation("X-OGLVoice-Timestamp", stamp.ToString());
                request.Headers.TryAddWithoutValidation("X-OGLVoice-Nonce", nonce);
                request.Headers.TryAddWithoutValidation("X-OGLVoice-Signature", signature);

                using HttpResponseMessage response = await m_Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Volatile.Write(ref m_Current, null);
                    m_Log.WarnFormat("[OGL-VOICE]: Provider-Discovery abgelehnt (HTTP {0}).", (int)response.StatusCode);
                    return;
                }

                if (response.Content.Headers.ContentLength > 8192)
                    throw new InvalidOperationException("Oversized discovery descriptor");
                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (body.Length > 8192)
                    throw new InvalidOperationException("Oversized discovery descriptor");

                string signed = response.Headers.TryGetValues("X-OGLVoice-Response-Signature", out var values)
                    ? System.Linq.Enumerable.FirstOrDefault(values) : null;
                if (!OglVoiceDiscoveryProof.VerifyResponse(m_Key, nonce, body, signed))
                    throw new InvalidOperationException("Invalid signed discovery response");

                OglVoiceProviderDescriptor provider =
                    JsonSerializer.Deserialize<OglVoiceProviderDescriptor>(body);
                if (provider == null)
                    throw new InvalidOperationException("Empty provider descriptor");
                provider.Validate();

                Volatile.Write(ref m_Current, provider);
                m_LastRefresh = DateTimeOffset.UtcNow;
            }
            catch (Exception e)
            {
                Volatile.Write(ref m_Current, null);
                m_Log.Warn("[OGL-VOICE]: Provider-Discovery fehlgeschlagen; keine unbestaetigten Einstellungen verwendet. " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref m_Refreshing, 0);
            }
        }

        public void Close()
        {
            Interlocked.Exchange(ref m_Disposed, 1);
            m_Timer?.Dispose();
            m_Http?.Dispose();
            Volatile.Write(ref m_Current, null);
            m_Scenes.Clear();
            m_Key = string.Empty;
        }
    }
}
