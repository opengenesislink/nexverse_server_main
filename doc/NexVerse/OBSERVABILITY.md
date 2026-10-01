# NexVerse Observability

NexVerse exposes two complementary production observability paths:

- Prometheus-compatible pull metrics through the optional `[NexMetrics]` endpoint.
- OpenTelemetry Protocol export through the optional `[NexTelemetry]` OTLP/HTTP JSON pipeline.

Both are disabled by default.

## Prometheus metrics

```ini
[NexMetrics]
Enabled = true
Path = "/internal/metrics"
```

The metrics endpoint is intended for a trusted monitoring network. Restrict it with firewall, reverse-proxy and TLS policy when it is reachable outside localhost/private infrastructure.

## OTLP/HTTP JSON collector export

```ini
[NexTelemetry]
Enabled = true
Protocol = "http/json"
Endpoint = "https://otel-collector.example:4318"
TracesEndpoint = ""
MetricsEndpoint = ""
ServiceName = "NexVerse.Robust"
ServiceInstanceId = "robust-main"
Headers = ""
ExportIntervalSeconds = 10
BatchSize = 256
QueueCapacity = 4096
TimeoutMilliseconds = 10000
MaxRetries = 3
AllowInsecure = false
```

With the common `Endpoint`, NexVerse sends traces to `/v1/traces` and metrics to `/v1/metrics`. `TracesEndpoint` and `MetricsEndpoint` override those URLs independently and are used exactly as configured.

The built-in exporter intentionally supports `http/json`. It emits OTLP JSON using lower-camel-case protobuf field names, hexadecimal trace/span IDs, integer enum values and decimal-string 64-bit nanosecond timestamps.

`AllowInsecure=false` rejects plaintext HTTP collectors. Set it to `true` only when the collector path is protected by a trusted private network or equivalent transport security.

`Headers` accepts comma-separated `key=value` pairs for collector authentication or metadata. Percent-encoding may be used when a value contains a comma. Header values are not written to audit records.

## Reliability and backpressure

Trace export is decoupled from request processing through a bounded in-memory queue. The queue is flushed in batches and on the configured interval. A full queue drops new spans instead of blocking the World API and increments `nexverse_otlp_spans_dropped_total`.

HTTP 408, 429, 502, 503 and 504 responses and connection/timeout failures are retried with bounded exponential backoff and jitter. `Retry-After` is honored when supplied, capped at 30 seconds.

Exporter health is visible through NexMetrics:

- `nexverse_otlp_queue_depth`
- `nexverse_otlp_spans_dropped_total`
- `nexverse_otlp_export_requests_total{signal,result}`
- `nexverse_otlp_export_records_total{signal,result}`
- `nexverse_otlp_worker_failures_total`

## Resource identity

Each OTLP resource carries `service.name`, `service.instance.id`, `service.version` and `nexverse.milestone`. The instrumentation scope is `NexVerse`.

## Security

Do not place resident passwords, access tokens, refresh tokens, OAuth client secrets, API keys or other credentials in Activity tags or metric labels.

Collector authentication belongs in the protected `Headers` configuration. Prefer HTTPS and restrict the collector to trusted NexVerse infrastructure.
