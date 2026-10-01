// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexVerse.Core;
using NexVerse.Core.Observability;

namespace NexVerse.Server.Api
{
    internal sealed class NexOtlpHttpOptions
    {
        public Uri Endpoint { get; set; }
        public Uri TracesEndpoint { get; set; }
        public Uri MetricsEndpoint { get; set; }
        public string ServiceName { get; set; } = "NexVerse.Robust";
        public string ServiceInstanceId { get; set; } = Environment.MachineName;
        public IReadOnlyDictionary<string, string> Headers { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int ExportIntervalSeconds { get; set; } = 10;
        public int BatchSize { get; set; } = 256;
        public int QueueCapacity { get; set; } = 4096;
        public int TimeoutMilliseconds { get; set; } = 10000;
        public int MaxRetries { get; set; } = 3;
        public bool AllowInsecure { get; set; }
        public bool AutoStart { get; set; } = true;
    }

    internal sealed class NexOtlpHttpExporter : IDisposable
    {
        private readonly NexMetricsRegistry m_Metrics;
        private readonly NexOtlpHttpOptions m_Options;
        private readonly Uri m_TracesEndpoint;
        private readonly Uri m_MetricsEndpoint;
        private readonly BlockingCollection<Dictionary<string, object>> m_Spans;
        private readonly CancellationTokenSource m_Stop = new CancellationTokenSource();
        private readonly HttpClient m_Client;
        private readonly ActivityListener m_Listener;
        private readonly Task m_Worker;
        private readonly object m_FlushSync = new object();
        private readonly long m_StartUnixNano;
        private int m_Disposed;

        public NexOtlpHttpExporter(
            NexMetricsRegistry metrics,
            NexOtlpHttpOptions options,
            HttpMessageHandler handler = null)
        {
            m_Metrics = metrics ??
                throw new ArgumentNullException(nameof(metrics));
            m_Options = options ??
                throw new ArgumentNullException(nameof(options));

            ValidateOptions(options);

            m_TracesEndpoint =
                ResolveEndpoint(
                    options.TracesEndpoint,
                    options.Endpoint,
                    "v1/traces");
            m_MetricsEndpoint =
                ResolveEndpoint(
                    options.MetricsEndpoint,
                    options.Endpoint,
                    "v1/metrics");

            ValidateTransport(m_TracesEndpoint, options.AllowInsecure);
            ValidateTransport(m_MetricsEndpoint, options.AllowInsecure);

            m_Spans =
                new BlockingCollection<Dictionary<string, object>>(
                    new ConcurrentQueue<Dictionary<string, object>>(),
                    Math.Max(128, options.QueueCapacity));

            m_Client = handler == null
                ? new HttpClient()
                : new HttpClient(handler, false);
            m_Client.Timeout =
                TimeSpan.FromMilliseconds(
                    Math.Max(250, options.TimeoutMilliseconds));

            m_StartUnixNano =
                UnixNanoseconds(DateTime.UtcNow);

            m_Listener = new ActivityListener
            {
                ShouldListenTo =
                    source =>
                        string.Equals(
                            source.Name,
                            NexTelemetry.ActivitySourceName,
                            StringComparison.Ordinal),
                Sample =
                    (ref ActivityCreationOptions<ActivityContext> _) =>
                        ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId =
                    (ref ActivityCreationOptions<string> _) =>
                        ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = OnActivityStopped
            };

            ActivitySource.AddActivityListener(m_Listener);

            if (options.AutoStart)
                m_Worker = Task.Run(WorkerLoop);
        }

        public void Flush()
        {
            if (Volatile.Read(ref m_Disposed) != 0)
                return;

            lock (m_FlushSync)
            {
                ExportQueuedSpans();
                ExportMetrics();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0)
                return;

            m_Listener.Dispose();
            m_Spans.CompleteAdding();
            m_Stop.Cancel();

            try
            {
                m_Worker?.Wait(
                    TimeSpan.FromMilliseconds(
                        Math.Max(
                            1000,
                            m_Options.TimeoutMilliseconds + 1000)));
            }
            catch
            {
            }

            lock (m_FlushSync)
            {
                ExportQueuedSpans();
                ExportMetrics();
            }

            m_Client.Dispose();
            m_Stop.Dispose();
            m_Spans.Dispose();
        }

        private void OnActivityStopped(Activity activity)
        {
            if (activity == null ||
                Volatile.Read(ref m_Disposed) != 0)
                return;

            try
            {
                if (!m_Spans.TryAdd(BuildSpan(activity)))
                {
                    m_Metrics.IncrementCounter(
                        "nexverse_otlp_spans_dropped_total",
                        "NexVerse trace spans dropped because the OTLP export queue is full.");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            m_Metrics.SetGauge(
                "nexverse_otlp_queue_depth",
                "Current NexVerse OTLP trace export queue depth.",
                m_Spans.Count);
        }

        private void WorkerLoop()
        {
            TimeSpan interval =
                TimeSpan.FromSeconds(
                    Math.Max(1, m_Options.ExportIntervalSeconds));

            try
            {
                while (!m_Stop.IsCancellationRequested)
                {
                    if (m_Spans.Count >= Math.Max(1, m_Options.BatchSize))
                    {
                        lock (m_FlushSync)
                            ExportQueuedSpans();
                    }

                    if (m_Stop.Token.WaitHandle.WaitOne(interval))
                        break;

                    lock (m_FlushSync)
                    {
                        ExportQueuedSpans();
                        ExportMetrics();
                    }
                }
            }
            catch (Exception)
            {
                m_Metrics.IncrementCounter(
                    "nexverse_otlp_worker_failures_total",
                    "Unexpected NexVerse OTLP exporter worker failures.");
            }
        }

        private void ExportQueuedSpans()
        {
            int batchSize = Math.Max(1, m_Options.BatchSize);

            while (m_Spans.Count > 0)
            {
                List<Dictionary<string, object>> batch =
                    new List<Dictionary<string, object>>(batchSize);

                while (batch.Count < batchSize &&
                       m_Spans.TryTake(out Dictionary<string, object> span))
                    batch.Add(span);

                if (batch.Count == 0)
                    break;

                m_Metrics.SetGauge(
                    "nexverse_otlp_queue_depth",
                    "Current NexVerse OTLP trace export queue depth.",
                    m_Spans.Count);

                Send(
                    m_TracesEndpoint,
                    JsonSerializer.SerializeToUtf8Bytes(
                        BuildTracePayload(batch)),
                    "traces",
                    batch.Count);
            }
        }

        private void ExportMetrics()
        {
            IReadOnlyList<NexMetricSnapshot> snapshots =
                m_Metrics.Snapshot();

            if (snapshots.Count == 0)
                return;

            long now = UnixNanoseconds(DateTime.UtcNow);
            List<object> metricPayloads =
                new List<object>(snapshots.Count);
            int dataPointCount = 0;

            foreach (NexMetricSnapshot metric in snapshots)
            {
                object[] points =
                    metric.Samples
                        .Select(sample =>
                            BuildMetricPoint(
                                sample,
                                now,
                                metric.Type == NexMetricType.Counter))
                        .ToArray();

                dataPointCount += points.Length;

                Dictionary<string, object> item =
                    new Dictionary<string, object>
                    {
                        ["name"] = metric.Name,
                        ["description"] = metric.Help
                    };

                if (metric.Type == NexMetricType.Counter)
                {
                    item["sum"] =
                        new Dictionary<string, object>
                        {
                            ["aggregationTemporality"] = 2,
                            ["isMonotonic"] = true,
                            ["dataPoints"] = points
                        };
                }
                else
                {
                    item["gauge"] =
                        new Dictionary<string, object>
                        {
                            ["dataPoints"] = points
                        };
                }

                metricPayloads.Add(item);
            }

            object payload =
                new
                {
                    resourceMetrics =
                        new[]
                        {
                            new
                            {
                                resource =
                                    new
                                    {
                                        attributes =
                                            ResourceAttributes()
                                    },
                                scopeMetrics =
                                    new[]
                                    {
                                        new
                                        {
                                            scope =
                                                InstrumentationScope(),
                                            metrics =
                                                metricPayloads
                                        }
                                    }
                            }
                        }
                };

            Send(
                m_MetricsEndpoint,
                JsonSerializer.SerializeToUtf8Bytes(payload),
                "metrics",
                dataPointCount);
        }

        private object BuildTracePayload(
            IReadOnlyList<Dictionary<string, object>> spans)
        {
            return new
            {
                resourceSpans =
                    new[]
                    {
                        new
                        {
                            resource =
                                new
                                {
                                    attributes =
                                        ResourceAttributes()
                                },
                            scopeSpans =
                                new[]
                                {
                                    new
                                    {
                                        scope =
                                            InstrumentationScope(),
                                        spans
                                    }
                                }
                        }
                    }
            };
        }

        private static Dictionary<string, object> BuildSpan(
            Activity activity)
        {
            long start = UnixNanoseconds(activity.StartTimeUtc);
            long end =
                UnixNanoseconds(
                    activity.StartTimeUtc +
                    activity.Duration);

            Dictionary<string, object> span =
                new Dictionary<string, object>
                {
                    ["traceId"] = activity.TraceId.ToHexString(),
                    ["spanId"] = activity.SpanId.ToHexString(),
                    ["name"] =
                        activity.DisplayName ??
                        activity.OperationName ??
                        "nexverse.activity",
                    ["kind"] = (int)activity.Kind + 1,
                    ["startTimeUnixNano"] =
                        start.ToString(
                            CultureInfo.InvariantCulture),
                    ["endTimeUnixNano"] =
                        end.ToString(
                            CultureInfo.InvariantCulture),
                    ["attributes"] =
                        Attributes(activity.TagObjects),
                    ["status"] = BuildStatus(activity)
                };

            if (activity.ParentSpanId != default)
                span["parentSpanId"] =
                    activity.ParentSpanId.ToHexString();

            if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
                span["traceState"] = activity.TraceStateString;

            object[] events =
                activity.Events
                    .Select(e =>
                        new Dictionary<string, object>
                        {
                            ["timeUnixNano"] =
                                UnixNanoseconds(e.Timestamp.UtcDateTime)
                                    .ToString(
                                        CultureInfo.InvariantCulture),
                            ["name"] = e.Name,
                            ["attributes"] =
                                Attributes(e.Tags)
                        })
                    .Cast<object>()
                    .ToArray();

            if (events.Length > 0)
                span["events"] = events;

            return span;
        }

        private object BuildMetricPoint(
            NexMetricSampleSnapshot sample,
            long timeUnixNano,
            bool cumulative)
        {
            Dictionary<string, object> point =
                new Dictionary<string, object>
                {
                    ["attributes"] =
                        Attributes(
                            sample.Labels.Select(
                                x =>
                                    new KeyValuePair<string, object>(
                                        x.Key,
                                        x.Value))),
                    ["timeUnixNano"] =
                        timeUnixNano.ToString(
                            CultureInfo.InvariantCulture),
                    ["asDouble"] = sample.Value
                };

            if (cumulative)
            {
                point["startTimeUnixNano"] =
                    m_StartUnixNano.ToString(
                        CultureInfo.InvariantCulture);
            }

            return point;
        }

        private object[] ResourceAttributes()
        {
            return new[]
            {
                KeyValue("service.name", m_Options.ServiceName),
                KeyValue("service.instance.id", m_Options.ServiceInstanceId),
                KeyValue("service.version", NexVersePlatform.ApiVersion),
                KeyValue("nexverse.milestone", NexVersePlatform.MilestoneCodename)
            };
        }

        private static object InstrumentationScope()
        {
            return new
            {
                name = NexTelemetry.ActivitySourceName,
                version = NexVersePlatform.ApiVersion
            };
        }

        private static object BuildStatus(Activity activity)
        {
            int code =
                activity.Status switch
                {
                    ActivityStatusCode.Ok => 1,
                    ActivityStatusCode.Error => 2,
                    _ => 0
                };

            if (string.IsNullOrWhiteSpace(activity.StatusDescription))
                return new { code };

            return new
            {
                code,
                message = activity.StatusDescription
            };
        }

        private static object[] Attributes(
            IEnumerable<KeyValuePair<string, object>> values)
        {
            if (values == null)
                return Array.Empty<object>();

            return values
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Key) &&
                    x.Value != null)
                .Select(x => KeyValue(x.Key, x.Value))
                .ToArray();
        }

        private static object KeyValue(
            string key,
            object value)
        {
            return new Dictionary<string, object>
            {
                ["key"] = key ?? string.Empty,
                ["value"] = AnyValue(value)
            };
        }

        private static object AnyValue(object value)
        {
            if (value is bool boolean)
                return new Dictionary<string, object>
                {
                    ["boolValue"] = boolean
                };

            if (value is byte ||
                value is sbyte ||
                value is short ||
                value is ushort ||
                value is int ||
                value is uint ||
                value is long ||
                value is ulong)
            {
                return new Dictionary<string, object>
                {
                    ["intValue"] =
                        Convert.ToString(
                            value,
                            CultureInfo.InvariantCulture)
                };
            }

            if (value is float ||
                value is double ||
                value is decimal)
            {
                return new Dictionary<string, object>
                {
                    ["doubleValue"] =
                        Convert.ToDouble(
                            value,
                            CultureInfo.InvariantCulture)
                };
            }

            return new Dictionary<string, object>
            {
                ["stringValue"] =
                    Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture) ??
                    string.Empty
            };
        }

        private void Send(
            Uri endpoint,
            byte[] payload,
            string signal,
            int recordCount)
        {
            int maxAttempts =
                Math.Max(1, m_Options.MaxRetries + 1);

            for (int attempt = 1;
                 attempt <= maxAttempts;
                 attempt++)
            {
                try
                {
                    using HttpRequestMessage request =
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            endpoint);

                    request.Content =
                        new ByteArrayContent(payload);
                    request.Content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/json");

                    foreach (KeyValuePair<string, string> header
                             in m_Options.Headers ??
                             new Dictionary<string, string>())
                    {
                        if (!string.IsNullOrWhiteSpace(header.Key))
                        {
                            request.Headers.TryAddWithoutValidation(
                                header.Key,
                                header.Value ?? string.Empty);
                        }
                    }

                    using HttpResponseMessage response =
                        m_Client.Send(
                            request,
                            HttpCompletionOption.ResponseHeadersRead);

                    if (response.IsSuccessStatusCode)
                    {
                        RecordExport(
                            signal,
                            "success",
                            recordCount);
                        return;
                    }

                    if (IsRetryable(response.StatusCode) &&
                        attempt < maxAttempts)
                    {
                        RecordExport(
                            signal,
                            "retry",
                            recordCount);
                        SleepBeforeRetry(response, attempt);
                        continue;
                    }

                    RecordExport(
                        signal,
                        "failed",
                        recordCount);
                    return;
                }
                catch (HttpRequestException)
                {
                    if (attempt < maxAttempts)
                    {
                        RecordExport(
                            signal,
                            "retry",
                            recordCount);
                        SleepBeforeRetry(null, attempt);
                        continue;
                    }

                    RecordExport(
                        signal,
                        "failed",
                        recordCount);
                    return;
                }
                catch (TaskCanceledException)
                {
                    if (attempt < maxAttempts)
                    {
                        RecordExport(
                            signal,
                            "retry",
                            recordCount);
                        SleepBeforeRetry(null, attempt);
                        continue;
                    }

                    RecordExport(
                        signal,
                        "failed",
                        recordCount);
                    return;
                }
            }
        }

        private void RecordExport(
            string signal,
            string result,
            int recordCount)
        {
            Dictionary<string, string> labels =
                new Dictionary<string, string>
                {
                    ["signal"] = signal,
                    ["result"] = result
                };

            m_Metrics.IncrementCounter(
                "nexverse_otlp_export_requests_total",
                "NexVerse OTLP/HTTP export requests by signal and result.",
                1,
                labels);

            m_Metrics.IncrementCounter(
                "nexverse_otlp_export_records_total",
                "NexVerse OTLP records included in export requests by signal and result.",
                Math.Max(0, recordCount),
                labels);
        }

        private static bool IsRetryable(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code == 408 ||
                   code == 429 ||
                   code == 502 ||
                   code == 503 ||
                   code == 504;
        }

        private static void SleepBeforeRetry(
            HttpResponseMessage response,
            int attempt)
        {
            TimeSpan delay =
                TimeSpan.FromMilliseconds(
                    Math.Min(
                        5000,
                        200 *
                        (1 << Math.Min(
                            4,
                            Math.Max(0, attempt - 1))) +
                        Random.Shared.Next(25, 176)));

            if (response?.Headers?.RetryAfter?.Delta
                is TimeSpan retryAfter &&
                retryAfter > TimeSpan.Zero)
            {
                delay =
                    retryAfter > TimeSpan.FromSeconds(30)
                        ? TimeSpan.FromSeconds(30)
                        : retryAfter;
            }

            Thread.Sleep(delay);
        }

        private static Uri ResolveEndpoint(
            Uri specific,
            Uri common,
            string signalPath)
        {
            if (specific != null)
                return specific;

            if (common == null)
                throw new ArgumentException(
                    "An OTLP endpoint is required.");

            string value = common.AbsoluteUri;
            if (!value.EndsWith("/", StringComparison.Ordinal))
                value += "/";

            return new Uri(
                value + signalPath,
                UriKind.Absolute);
        }

        private static void ValidateTransport(
            Uri endpoint,
            bool allowInsecure)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri)
                throw new ArgumentException(
                    "OTLP endpoints must be absolute URLs.");

            if (!string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "OTLP/HTTP endpoints must use http or https.");
            }

            if (!allowInsecure &&
                string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Plain HTTP OTLP export is disabled. Use HTTPS or set AllowInsecure=true only on a trusted private transport.");
            }
        }

        private static void ValidateOptions(
            NexOtlpHttpOptions options)
        {
            if (options.Endpoint == null &&
                options.TracesEndpoint == null &&
                options.MetricsEndpoint == null)
            {
                throw new ArgumentException(
                    "At least one OTLP endpoint must be configured.");
            }

            if (string.IsNullOrWhiteSpace(options.ServiceName))
                throw new ArgumentException(
                    "OTLP service name is required.");

            if (options.ExportIntervalSeconds < 1 ||
                options.BatchSize < 1 ||
                options.QueueCapacity < 128 ||
                options.TimeoutMilliseconds < 250 ||
                options.MaxRetries < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "OTLP timing, batching, queue and retry settings are outside supported bounds.");
            }
        }

        private static long UnixNanoseconds(DateTime utc)
        {
            long ticks =
                utc.ToUniversalTime().Ticks -
                DateTime.UnixEpoch.Ticks;

            return checked(ticks * 100L);
        }
    }
}
