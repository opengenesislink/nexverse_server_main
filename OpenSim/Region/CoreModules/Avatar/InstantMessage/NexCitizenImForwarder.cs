// SPDX-License-Identifier: MPL-2.0
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage
{
    /// <summary>
    /// Best-effort, bounded simulator -> Robust notification after successful
    /// in-world IM acceptance. Viewer IM delivery itself is never blocked by
    /// the optional portal archive. The endpoint validates a shared HMAC.
    /// </summary>
    internal sealed class NexCitizenImForwarder
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(NexCitizenImForwarder));
        private static readonly HttpClient s_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private readonly Uri m_Endpoint;
        private readonly byte[] m_Key;
        private readonly SemaphoreSlim m_Queue = new SemaphoreSlim(48, 48);

        private NexCitizenImForwarder(Uri endpoint, string secret)
        {
            m_Endpoint = endpoint;
            m_Key = Encoding.UTF8.GetBytes(secret);
        }

        internal static NexCitizenImForwarder FromConfig(IConfigSource config)
        {
            IConfig settings = config.Configs["NexPortalIM"];
            if (settings == null || !settings.GetBoolean("Enabled", false))
                return null;

            string raw = settings.GetString("IngestUrl", "").Trim();
            string secret = Environment.GetEnvironmentVariable("NEX_PORTAL_IM_SECRET");
            if (string.IsNullOrWhiteSpace(secret))
                secret = settings.GetString("SharedSecret", "");

            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri url) ||
                (url.Scheme != Uri.UriSchemeHttps &&
                 !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
                url.AbsolutePath != "/internal/nexportal/im/v1" ||
                Encoding.UTF8.GetByteCount(secret) < 32)
            {
                m_Log.Warn("[NEX-PORTAL-IM]: Forwarding disabled: require HTTPS/loopback URL and 32-byte secret.");
                return null;
            }
            return new NexCitizenImForwarder(url, secret);
        }

        internal void Accepted(GridInstantMessage im)
        {
            if (im == null || im.fromGroup ||
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent ||
                im.fromAgentID == Guid.Empty || im.toAgentID == Guid.Empty ||
                im.fromAgentID == im.toAgentID ||
                string.IsNullOrWhiteSpace(im.message) ||
                Encoding.UTF8.GetByteCount(im.message) > 1024)
                return;

            if (!m_Queue.Wait(0))
            {
                m_Log.Warn("[NEX-PORTAL-IM]: Event queue full; skipping optional web archive notification.");
                return;
            }

            string json = JsonSerializer.Serialize(new
            {
                event_id = Guid.NewGuid(),
                from_agent_id = im.fromAgentID,
                to_agent_id = im.toAgentID,
                dialog = (int)im.dialog,
                from_group = false,
                message = im.message
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
                                System.Globalization.CultureInfo.InvariantCulture);
                            byte[] head = Encoding.ASCII.GetBytes(timestamp + "\n");
                            byte[] signed = new byte[head.Length + body.Length];
                            Buffer.BlockCopy(head, 0, signed, 0, head.Length);
                            Buffer.BlockCopy(body, 0, signed, head.Length, body.Length);
                            using var hmac = new HMACSHA256(m_Key);
                            string signature = Convert.ToBase64String(hmac.ComputeHash(signed));

                            using var post = new HttpRequestMessage(HttpMethod.Post, m_Endpoint);
                            post.Headers.TryAddWithoutValidation("X-NexPortal-IM-Time", timestamp);
                            post.Headers.TryAddWithoutValidation("X-NexPortal-IM-Signature", signature);
                            post.Content = new ByteArrayContent(body);
                            post.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                            using HttpResponseMessage result = await s_Http.SendAsync(post).ConfigureAwait(false);
                            if (result.IsSuccessStatusCode)
                                return;
                            if ((int)result.StatusCode < 500)
                                return;
                        }
                        catch (HttpRequestException)
                        {
                            // Retry same event_id. Robust uses per-owner idempotency.
                        }
                        catch (TaskCanceledException)
                        {
                            // Bounded HTTP timeout, not a viewer IM failure.
                        }
                        await Task.Delay(250 * (attempt + 1)).ConfigureAwait(false);
                    }
                    m_Log.Warn("[NEX-PORTAL-IM]: Web archive notification failed after retries (IM delivery unaffected).");
                }
                finally
                {
                    m_Queue.Release();
                }
            });
        }
    }
}
