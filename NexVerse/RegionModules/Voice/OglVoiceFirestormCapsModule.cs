// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using NexVerse.Core.Voice;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace NexVerse.RegionModules.Voice
{
    /// <summary>
    /// Firestorm-compatible LLSD voice CAPS to a trusted, independently
    /// operated OGLVoice WebRTC media gateway. The gateway MUST terminate
    /// Firestorm SDP/ICE/DTLS/SRTP/SCTP and bridge media to LiveKit. Merely
    /// configuring a LiveKit SFU URL is not sufficient.
    ///
    /// The module never distributes LiveKit administrator credentials.
    /// No CAPS or VoiceServerType are advertised until an authenticated
    /// provider explicitly enables the compatible media gateway.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
        Id = "OglVoiceFirestormCapsModule")]
    public sealed class OglVoiceFirestormCapsModule : INonSharedRegionModule
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(OglVoiceFirestormCapsModule));
        private sealed class VoiceSession
        {
            public string ViewerSession;
            public Guid AgentSessionId;
        }

        private readonly ConcurrentDictionary<UUID, VoiceSession> m_Sessions = new();
        private readonly SemaphoreSlim m_Requests = new(16, 16);
        private static readonly byte[] s_Undefined = Encoding.UTF8.GetBytes("<llsd><undef /></llsd>");
        private Scene m_Scene;
        private HttpClient m_Http;
        private ISimulatorFeaturesModule m_Features;
        private bool m_Enabled;
        private string m_NodeId;
        private string m_Key;

        public string Name => "OGLVoice Firestorm WebRTC Capability Bridge";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig options = source.Configs["OGLVoiceViewer"];
            if (options?.GetBoolean("Enabled", true) == false)
                return;
            IConfig node = source.Configs["NexVerseNodeAgent"];
            IConfig ogl = source.Configs["OGLVoice"];
            bool grid = node?.GetBoolean("Enabled", false) == true;
            bool standalone = ogl?.GetBoolean("Enabled", false) == true &&
                string.Equals(ogl.GetString("Mode", ""), "Standalone",
                    StringComparison.OrdinalIgnoreCase);
            if (!grid && !standalone)
                return;
            try
            {
                m_NodeId = grid
                    ? node.GetString("NodeId", Environment.MachineName).Trim()
                    : Environment.GetEnvironmentVariable("OGLVOICE_MEDIA_BRIDGE_NODE_ID")?.Trim()
                        ?? Environment.MachineName;
                m_Key = grid
                    ? node.GetString("SharedKey", "")
                    : Environment.GetEnvironmentVariable("OGLVOICE_MEDIA_BRIDGE_SHARED_KEY");
                OglVoiceMediaProof.Sign(m_Key, m_NodeId,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OglVoiceDiscoveryProof.NewNonce(), new byte[] { 1 });
                m_Http = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false
                })
                {
                    Timeout = TimeSpan.FromSeconds(6)
                };
                m_Enabled = true;
            }
            catch (Exception ex)
            {
                m_Log.Warn("[OGL-VOICE]: Firestorm-CAPS bleiben deaktiviert: " + ex.Message);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled || scene == null)
                return;
            m_Scene = scene;
            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled || m_Scene != scene)
                return;
            m_Features = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (m_Features != null)
                m_Features.OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest;
        }

        public void PostInitialise() { }

        public void RemoveRegion(Scene scene)
        {
            if (m_Scene != scene)
                return;
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            if (m_Features != null)
                m_Features.OnSimulatorFeaturesRequest -= OnSimulatorFeaturesRequest;
            m_Features = null;
            m_Scene = null;
            m_Sessions.Clear();
        }

        public void Close()
        {
            if (m_Scene != null)
                RemoveRegion(m_Scene);
            m_Http?.Dispose();
            m_Enabled = false;
            m_Key = null;
        }

        private OglVoiceProviderDescriptor ReadyProvider()
        {
            if (!m_Enabled || m_Scene == null || m_Http == null)
                return null;
            OglVoiceProviderDescriptor provider =
                m_Scene.RequestModuleInterface<IOglVoiceProviderLookup>()?.CurrentProvider;
            if (provider?.viewer_capability != "firestorm-webrtc-v1" ||
                !OglVoiceDiscoveryProof.IsSafeServiceUri(provider.media_gateway_url))
                return null;
            return provider;
        }

        private void OnSimulatorFeaturesRequest(UUID avatar, ref OSDMap features)
        {
            OglVoiceProviderDescriptor provider = ReadyProvider();
            if (provider == null)
                return;
            if (OglVoiceRegionAdmission.TryCreate(m_Scene, avatar,
                provider.tenant_id, provider.hypergrid_guests, out _))
                features["VoiceServerType"] = OSD.FromString("webrtc");
        }

        private void RegisterCaps(UUID avatar, Caps caps)
        {
            if (ReadyProvider() == null)
                return;
            caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => Handle(req, resp, avatar, "provision")));
            caps.RegisterSimpleHandler("VoiceSignalingRequest",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => Handle(req, resp, avatar, "signal")));
        }

        private void Handle(IOSHttpRequest request, IOSHttpResponse response,
            UUID avatar, string operation)
        {
            response.ContentType = "application/llsd+xml";
            response.KeepAlive = false;
            response.AddHeader("Cache-Control", "no-store");
            if (request.HttpMethod != "POST")
            {
                Error(response, HttpStatusCode.MethodNotAllowed);
                return;
            }

            OglVoiceProviderDescriptor provider = ReadyProvider();
            if (provider == null || m_Scene == null ||
                !OglVoiceRegionAdmission.TryCreate(m_Scene, avatar,
                    provider.tenant_id, provider.hypergrid_guests,
                    out OglVoiceAdmission admission))
            {
                Error(response, HttpStatusCode.Forbidden);
                return;
            }
            if (!m_Requests.Wait(0))
            {
                Error(response, HttpStatusCode.ServiceUnavailable);
                return;
            }

            try
            {
                // Read a bounded LLSD document; never proxy untrusted raw XML.
                using MemoryStream stream = new();
                byte[] buffer = new byte[4096];
                int n;
                while ((n = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (stream.Length + n > 64 * 1024)
                    {
                        Error(response, HttpStatusCode.RequestEntityTooLarge);
                        return;
                    }
                    stream.Write(buffer, 0, n);
                }

                stream.Position = 0;
                if (OSDParser.DeserializeLLSDXml(stream) is not OSDMap map ||
                    !map.TryGetValue("voice_server_type", out OSD type) ||
                    type.AsString() != "webrtc")
                {
                    Error(response, HttpStatusCode.BadRequest);
                    return;
                }

                OglVoiceMediaExchange message = new()
                {
                    admission = admission,
                    operation = operation == "signal" ? "trickle" : "offer"
                };

                if (operation == "provision")
                {
                    if (map.TryGetValue("logout", out OSD logout) && logout.AsBoolean())
                    {
                        if (!m_Sessions.TryGetValue(avatar, out VoiceSession existing) ||
                            existing.AgentSessionId != admission.SessionId ||
                            !map.TryGetValue("viewer_session", out OSD exitSession) ||
                            exitSession.AsString() != existing.ViewerSession)
                        {
                            Error(response, HttpStatusCode.Forbidden);
                            return;
                        }
                        message.operation = "leave";
                        message.viewer_session = existing.ViewerSession;
                        m_Sessions.TryRemove(avatar, out _);
                    }
                    else
                    {
                        if (!map.TryGetValue("channel_type", out OSD channel) ||
                            channel.AsString() != "local" ||
                            !map.TryGetValue("jsep", out OSD jsep) ||
                            jsep is not OSDMap offer ||
                            !offer.TryGetValue("type", out OSD offerType) ||
                            offerType.AsString() != "offer" ||
                            !offer.TryGetValue("sdp", out OSD sdp))
                        {
                            Error(response, HttpStatusCode.BadRequest);
                            return;
                        }
                        message.sdp_offer = sdp.AsString();
                        OglVoiceFirestormWire.ValidateOffer(message.sdp_offer);
                        // Viewer-supplied parcel IDs never choose the voice room:
                        // the receiving Scene's verified avatar position does.
                    }
                }
                else
                {
                    if (!m_Sessions.TryGetValue(avatar, out VoiceSession existing) ||
                        existing.AgentSessionId != admission.SessionId ||
                        !map.TryGetValue("viewer_session", out OSD viewerSession) ||
                        viewerSession.AsString() != existing.ViewerSession)
                    {
                        Error(response, HttpStatusCode.Forbidden);
                        return;
                    }
                    message.viewer_session = existing.ViewerSession;
                    List<OglVoiceIceCandidate> candidates = new();
                    if (map.TryGetValue("candidates", out OSD values) && values is OSDArray ice)
                    {
                        if (ice.Count > OglVoiceFirestormWire.MaximumCandidates)
                        {
                            Error(response, HttpStatusCode.RequestEntityTooLarge);
                            return;
                        }
                        foreach (OSD item in ice)
                        {
                            if (item is not OSDMap entry)
                                throw new ArgumentException("ICE entry is not a map");
                            candidates.Add(new OglVoiceIceCandidate
                            {
                                candidate = entry["candidate"].AsString(),
                                sdpMid = entry["sdpMid"].AsString(),
                                sdpMLineIndex = entry["sdpMLineIndex"].AsInteger()
                            });
                        }
                    }
                    bool completed = map.TryGetValue("candidate", out OSD done) &&
                        done is OSDMap completedMap &&
                        completedMap.TryGetValue("completed", out OSD flag) && flag.AsBoolean();
                    OglVoiceFirestormWire.ValidateCandidates(candidates, completed);
                    message.candidates = candidates.ToArray();
                    message.ice_completed = completed;
                }

                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
                if (payload.Length > OglVoiceMediaProof.MaximumPayloadBytes)
                    throw new ArgumentException("Voice media exchange exceeds bounded request");
                // Separate HTTPS media service verifies the authenticated
                // node+body. No LiveKit API secret is sent through this CAP.
                using HttpRequestMessage backend = new(
                    HttpMethod.Post, new Uri(provider.media_gateway_url));
                backend.Content = new ByteArrayContent(payload);
                backend.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string nonce = OglVoiceDiscoveryProof.NewNonce();
                backend.Headers.TryAddWithoutValidation("X-OGLVoice-Node", m_NodeId);
                backend.Headers.TryAddWithoutValidation("X-OGLVoice-Timestamp",
                    stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                backend.Headers.TryAddWithoutValidation("X-OGLVoice-Nonce", nonce);
                backend.Headers.TryAddWithoutValidation("X-OGLVoice-Signature",
                    OglVoiceMediaProof.Sign(m_Key, m_NodeId, stamp, nonce, payload));

                using HttpResponseMessage result = m_Http.Send(
                    backend, HttpCompletionOption.ResponseHeadersRead);
                if (!result.IsSuccessStatusCode)
                {
                    Error(response, result.StatusCode == HttpStatusCode.Conflict
                        ? HttpStatusCode.Conflict : HttpStatusCode.ServiceUnavailable);
                    return;
                }
                if (result.Content.Headers.ContentLength > OglVoiceFirestormWire.MaximumResponseBytes)
                    throw new ArgumentException("Oversized media response");
                byte[] answer;
                using (Stream incoming = result.Content.ReadAsStream())
                using (MemoryStream bounded = new())
                {
                    byte[] chunk = new byte[4096];
                    int count;
                    while ((count = incoming.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (bounded.Length + count > OglVoiceFirestormWire.MaximumResponseBytes)
                            throw new ArgumentException("Oversized media response");
                        bounded.Write(chunk, 0, count);
                    }
                    answer = bounded.ToArray();
                }
                OglVoiceMediaReply media =
                    OglVoiceFirestormWire.ParseAnswer(answer, message.operation == "offer");

                if (message.operation == "offer")
                {
                    m_Sessions[avatar] = new VoiceSession
                    {
                        ViewerSession = media.viewer_session,
                        AgentSessionId = admission.SessionId
                    };
                    response.RawBuffer = Encoding.UTF8.GetBytes(
                        OSDParser.SerializeLLSDXmlString(new OSDMap
                        {
                            ["jsep"] = new OSDMap
                            {
                                ["type"] = OSD.FromString("answer"),
                                ["sdp"] = OSD.FromString(media.sdp_answer)
                            },
                            ["viewer_session"] = OSD.FromString(media.viewer_session)
                        }));
                }
                else
                {
                    response.RawBuffer = s_Undefined;
                }
                response.StatusCode = (int)HttpStatusCode.OK;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is System.Xml.XmlException ||
                                       ex is JsonException)
            {
                Error(response, HttpStatusCode.BadRequest);
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                       ex is TaskCanceledException ||
                                       ex is IOException)
            {
                m_Log.Warn("[OGL-VOICE]: WebRTC media bridge unavailable: " + ex.GetType().Name);
                Error(response, HttpStatusCode.ServiceUnavailable);
            }
            finally
            {
                m_Requests.Release();
            }
        }

        private static void Error(IOSHttpResponse resp, HttpStatusCode code)
        {
            resp.StatusCode = (int)code;
            resp.RawBuffer = s_Undefined;
        }
    }
}
