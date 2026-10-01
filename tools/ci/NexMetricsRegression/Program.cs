using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexVerse.Core.Observability;
using NexVerse.Server.Api;

internal static class Program
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public sealed class Capture
        {
            public Uri Uri { get; set; }
            public string MediaType { get; set; }
            public string Body { get; set; }
        }

        public List<Capture> Requests { get; } =
            new List<Capture>();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body =
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(
                        cancellationToken);

            Requests.Add(
                new Capture
                {
                    Uri = request.RequestUri,
                    MediaType =
                        request.Content?.Headers?.ContentType?.MediaType,
                    Body = body
                });

            return new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content =
                    new StringContent("{}")
            };
        }
    }

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        NexMetricsRegistry metrics =
            new NexMetricsRegistry();

        metrics.IncrementCounter(
            "nexverse_requests_total",
            "Regression requests.",
            2,
            new Dictionary<string, string>
            {
                ["route"] = "/api/v1"
            });

        metrics.IncrementCounter(
            "nexverse_requests_total",
            "Regression requests.",
            3,
            new Dictionary<string, string>
            {
                ["route"] = "/api/v1"
            });

        metrics.SetGauge(
            "nexverse_queue_depth",
            "Regression queue depth.",
            7);

        string rendered =
            metrics.RenderPrometheus();

        Require(
            rendered.Contains(
                "# TYPE nexverse_requests_total counter"),
            "counter type missing");

        Require(
            rendered.Contains(
                "nexverse_requests_total{route=\"/api/v1\"} 5"),
            "counter aggregation failed");

        Require(
            rendered.Contains(
                "# TYPE nexverse_queue_depth gauge"),
            "gauge type missing");

        Require(
            rendered.Contains(
                "nexverse_queue_depth 7"),
            "gauge value missing");

        IReadOnlyList<NexMetricSnapshot> snapshot =
            metrics.Snapshot();

        Require(
            snapshot.Any(x =>
                x.Name ==
                "nexverse_requests_total" &&
                x.Samples.Any(s =>
                    Math.Abs(s.Value - 5) < 0.0001)),
            "metric snapshot missing counter value");

        bool observed = false;

        using ActivityListener listener =
            new ActivityListener
            {
                ShouldListenTo =
                    source =>
                        source.Name ==
                        NexTelemetry.ActivitySourceName,
                Sample =
                    (ref ActivityCreationOptions<ActivityContext> _) =>
                        ActivitySamplingResult.AllData,
                ActivityStarted =
                    _ => observed = true
            };

        ActivitySource.AddActivityListener(listener);

        using (Activity activity =
               NexTelemetry.ActivitySource.StartActivity(
                   "regression"))
        {
            Require(
                activity != null,
                "ActivitySource did not create an activity");

            activity.SetTag(
                "nexverse.test",
                true);
        }

        Require(
            observed,
            "Activity listener did not observe NexVerse activity");

        CaptureHandler capture =
            new CaptureHandler();

        NexOtlpHttpOptions options =
            new NexOtlpHttpOptions
            {
                Endpoint =
                    new Uri(
                        "http://collector.example:4318",
                        UriKind.Absolute),
                ServiceName =
                    "NexVerse.Regression",
                ServiceInstanceId =
                    "ci",
                ExportIntervalSeconds = 3600,
                BatchSize = 16,
                QueueCapacity = 128,
                TimeoutMilliseconds = 1000,
                MaxRetries = 0,
                AllowInsecure = true,
                AutoStart = false
            };

        using (NexOtlpHttpExporter exporter =
               new NexOtlpHttpExporter(
                   metrics,
                   options,
                   capture))
        {
            using (Activity activity =
                   NexTelemetry.ActivitySource.StartActivity(
                       "otlp.regression",
                       ActivityKind.Server))
            {
                Require(
                    activity != null,
                    "OTLP listener did not sample activity");

                activity.SetTag(
                    "http.request.method",
                    "GET");

                activity.SetTag(
                    "nexverse.regression",
                    1L);

                activity.SetStatus(
                    ActivityStatusCode.Ok);
            }

            exporter.Flush();

            Require(
                capture.Requests.Count == 2,
                "expected one trace and one metric export");

            CaptureHandler.Capture trace =
                capture.Requests.Single(x =>
                    x.Uri.AbsolutePath.EndsWith(
                        "/v1/traces",
                        StringComparison.Ordinal));

            CaptureHandler.Capture metric =
                capture.Requests.Single(x =>
                    x.Uri.AbsolutePath.EndsWith(
                        "/v1/metrics",
                        StringComparison.Ordinal));

            Require(
                trace.MediaType == "application/json" &&
                metric.MediaType == "application/json",
                "OTLP HTTP JSON content type missing");

            using JsonDocument traceJson =
                JsonDocument.Parse(trace.Body);

            JsonElement span =
                traceJson.RootElement
                    .GetProperty("resourceSpans")[0]
                    .GetProperty("scopeSpans")[0]
                    .GetProperty("spans")[0];

            Require(
                span.GetProperty("traceId")
                    .GetString()?.Length == 32,
                "OTLP traceId is not 32 hex characters");

            Require(
                span.GetProperty("spanId")
                    .GetString()?.Length == 16,
                "OTLP spanId is not 16 hex characters");

            Require(
                span.GetProperty("kind")
                    .GetInt32() == 2,
                "server activity was not mapped to OTLP SPAN_KIND_SERVER");

            Require(
                span.GetProperty("startTimeUnixNano")
                    .ValueKind == JsonValueKind.String,
                "OTLP nanosecond timestamp must be a decimal string");

            using JsonDocument metricJson =
                JsonDocument.Parse(metric.Body);

            JsonElement exportedMetrics =
                metricJson.RootElement
                    .GetProperty("resourceMetrics")[0]
                    .GetProperty("scopeMetrics")[0]
                    .GetProperty("metrics");

            Require(
                exportedMetrics
                    .EnumerateArray()
                    .Any(x =>
                        x.GetProperty("name").GetString() ==
                        "nexverse_requests_total"),
                "OTLP metric payload missing regression counter");
        }

        bool blockedInsecure = false;

        try
        {
            using NexOtlpHttpExporter unused =
                new NexOtlpHttpExporter(
                    metrics,
                    new NexOtlpHttpOptions
                    {
                        Endpoint =
                            new Uri(
                                "http://collector.example:4318"),
                        AllowInsecure = false,
                        AutoStart = false
                    },
                    new CaptureHandler());
        }
        catch (InvalidOperationException)
        {
            blockedInsecure = true;
        }

        Require(
            blockedInsecure,
            "plain HTTP OTLP transport was not blocked by default");

        Console.WriteLine(
            "NexMetrics/OpenTelemetry OTLP regression: OK");

        return 0;
    }
}
