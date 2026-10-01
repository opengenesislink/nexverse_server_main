// SPDX-License-Identifier: MPL-2.0

using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using NexVerse.Core.Observability;
using NexVerse.Core.Security;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal static class NexApiRequestContext
    {
        private static readonly AsyncLocal<string> s_CorrelationId =
            new AsyncLocal<string>();

        public static string CurrentCorrelationId =>
            s_CorrelationId.Value ?? string.Empty;

        public static string Begin(IOSHttpResponse response)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            s_CorrelationId.Value = correlationId;

            response?.AddHeader(
                "X-NexVerse-Api-Version",
                Core.NexVersePlatform.ApiVersion);
            response?.AddHeader("X-Correlation-Id", correlationId);

            return correlationId;
        }

        public static string Ensure(IOSHttpResponse response)
        {
            if (!string.IsNullOrWhiteSpace(s_CorrelationId.Value))
                return s_CorrelationId.Value;

            return Begin(response);
        }

        public static void End()
        {
            s_CorrelationId.Value = null;
        }
    }

    internal sealed class NexApiRequestGate
    {
        private static readonly JsonSerializerOptions s_Json =
            new JsonSerializerOptions { WriteIndented = true };

        private readonly bool m_Enabled;
        private readonly bool m_TrustForwardedFor;
        private readonly NexFixedWindowRateLimiter m_Limiter;
        private long m_RequestCount;

        public NexApiRequestGate(
            bool enabled,
            int requestsPerWindow,
            int windowSeconds,
            bool trustForwardedFor)
        {
            m_Enabled = enabled;
            m_TrustForwardedFor = trustForwardedFor;
            m_Limiter = new NexFixedWindowRateLimiter(
                Math.Max(1, requestsPerWindow),
                Math.Max(1, windowSeconds));
        }

        public SimpleStreamMethod Wrap(SimpleStreamMethod next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, response) =>
            {
                NexApiRequestContext.Begin(response);
                try
                {
                    if (!TryEnter(request, response))
                        return;

                    next(request, response);
                }
                finally
                {
                    NexApiRequestContext.End();
                }
            };
        }

        private bool TryEnter(
            IOSHttpRequest request,
            IOSHttpResponse response)
        {
            if (!m_Enabled)
                return true;

            string key = ResolveClientKey(request);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            NexRateLimitDecision decision =
                m_Limiter.TryConsume(key, now);

            response.AddHeader(
                "RateLimit-Limit",
                decision.Limit.ToString());
            response.AddHeader(
                "RateLimit-Remaining",
                decision.Remaining.ToString());
            response.AddHeader(
                "RateLimit-Reset",
                Math.Max(0, decision.ResetAt - now).ToString());

            response.AddHeader(
                "X-RateLimit-Limit",
                decision.Limit.ToString());
            response.AddHeader(
                "X-RateLimit-Remaining",
                decision.Remaining.ToString());
            response.AddHeader(
                "X-RateLimit-Reset",
                decision.ResetAt.ToString());

            long requestNumber = Interlocked.Increment(ref m_RequestCount);
            if ((requestNumber & 1023) == 0)
            {
                long currentWindowStart =
                    now - (now % m_Limiter.WindowSeconds);
                m_Limiter.RemoveIdleBuckets(
                    currentWindowStart - m_Limiter.WindowSeconds);
            }

            if (decision.Accepted)
                return true;

            response.AddHeader(
                "Retry-After",
                decision.RetryAfterSeconds.ToString());

            NexMetricsRegistry.Default.IncrementCounter(
                "nexverse_world_api_rate_limit_rejections_total",
                "World API requests rejected by rate limiting.");

            response.KeepAlive = false;
            response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            response.ContentType = "application/json; charset=utf-8";
            response.RawBuffer = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    error = "rate_limited",
                    message = "Too many requests.",
                    retry_after = decision.RetryAfterSeconds,
                    correlation_id =
                        NexApiRequestContext.CurrentCorrelationId
                },
                s_Json);

            return false;
        }

        private string ResolveClientKey(IOSHttpRequest request)
        {
            if (m_TrustForwardedFor)
            {
                string forwarded =
                    request?.Headers?["X-Forwarded-For"];

                if (!string.IsNullOrWhiteSpace(forwarded))
                {
                    string first = forwarded.Split(',')[0].Trim();
                    if (IPAddress.TryParse(first, out IPAddress address))
                        return address.ToString();
                }
            }

            return request?.RemoteIPEndPoint?.Address?.ToString()
                ?? "unknown";
        }
    }
}
