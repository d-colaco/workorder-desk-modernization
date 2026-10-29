using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Microsoft.Extensions.DependencyInjection;
using Sync.Contracts;

namespace Lab.Observability;

public static class ObservabilityExtensions
{
    /// <summary>Wires the lab's spans and metrics. Export is on only when the standard OTEL_EXPORTER_OTLP_ENDPOINT
    /// variable is set (the Makefile points it at the collector on port 32135, protocol http/protobuf).
    /// Without it the instrumentation is still in the code but nothing leaves the process.</summary>
    public static OpenTelemetryBuilder AddLabTelemetry(this IServiceCollection services, string serviceName)
    {
        var export = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
        return services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource(LabTelemetry.Name).AddSqlClientInstrumentation();
                if (export) t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddMeter(LabTelemetry.Name)
                 // The SDK's default buckets are tuned for milliseconds; ours are seconds.
                 .AddView("dclab.sync.lag", new ExplicitBucketHistogramConfiguration { Boundaries = LabTelemetry.LagBuckets });
                if (export) m.AddOtlpExporter();
            });
    }
}
