// SPDX-License-Identifier: MPL-2.0

using System;
using System.Net;
using System.Text;
using NexVerse.Core.Observability;
using OpenSim.Framework.Servers.HttpServer;

namespace NexVerse.Server.Api
{
    internal sealed class NexMetricsEndpoint
    {
        private readonly NexMetricsRegistry m_Metrics;

        public NexMetricsEndpoint(NexMetricsRegistry metrics)
        {
            m_Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;

            if (request == null || !string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.ContentType = "text/plain; charset=utf-8";
                response.RawBuffer = Encoding.UTF8.GetBytes("method_not_allowed\n");
                return;
            }

            string rendered = m_Metrics.RenderPrometheus();
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            response.RawBuffer = Encoding.UTF8.GetBytes(rendered);
        }
    }
}
