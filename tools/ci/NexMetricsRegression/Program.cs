using System;
using System.Collections.Generic;
using System.Diagnostics;
using NexVerse.Core.Observability;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        NexMetricsRegistry metrics = new NexMetricsRegistry();
        metrics.IncrementCounter(
            "nexverse_requests_total",
            "Regression requests.",
            2,
            new Dictionary<string, string> { ["route"] = "/api/v1" });
        metrics.IncrementCounter(
            "nexverse_requests_total",
            "Regression requests.",
            3,
            new Dictionary<string, string> { ["route"] = "/api/v1" });
        metrics.SetGauge(
            "nexverse_queue_depth",
            "Regression queue depth.",
            7);

        string rendered = metrics.RenderPrometheus();

        Require(rendered.Contains("# TYPE nexverse_requests_total counter"), "counter type missing");
        Require(rendered.Contains("nexverse_requests_total{route=\"/api/v1\"} 5"), "counter aggregation failed");
        Require(rendered.Contains("# TYPE nexverse_queue_depth gauge"), "gauge type missing");
        Require(rendered.Contains("nexverse_queue_depth 7"), "gauge value missing");

        bool observed = false;
        using ActivityListener listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => observed = true
        };

        ActivitySource.AddActivityListener(listener);
        using (Activity activity = NexTelemetry.ActivitySource.StartActivity("regression"))
        {
            Require(activity != null, "ActivitySource did not create an activity");
            activity.SetTag("nexverse.test", true);
        }

        Require(observed, "Activity listener did not observe NexVerse activity");

        Console.WriteLine("NexMetrics/OpenTelemetry foundation regression: OK");
        return 0;
    }
}
