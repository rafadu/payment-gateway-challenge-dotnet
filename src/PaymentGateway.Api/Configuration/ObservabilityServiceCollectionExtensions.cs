using Microsoft.Extensions.DependencyInjection.Extensions;

using OpenTelemetry.Metrics;

using PaymentGateway.Api.Metrics;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the gateway's custom metrics instrumentation (ADR-0007). <see cref="PaymentMetrics"/>
/// is a process-wide singleton over an <see cref="System.Diagnostics.Metrics.IMeterFactory"/>-created
/// <see cref="System.Diagnostics.Metrics.Meter"/>, so every call site shares one set of instruments.
/// The vendor-neutral <c>System.Diagnostics.Metrics</c> API means the exporter is wired separately,
/// without touching instrumented code — here OpenTelemetry with a Prometheus scraping endpoint,
/// swappable for OTLP/Application Insights/Datadog by changing only this method.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services)
    {
        // Registers IMeterFactory (the seam PaymentMetrics and MetricCollector both create/observe
        // meters through). The generic host already adds it in a running app; calling it here keeps
        // the registration self-contained and correct in isolated (unit/DI) composition too.
        services.AddMetrics();
        services.TryAddSingleton<PaymentMetrics>();

        // Export the custom meter over OpenTelemetry to a Prometheus scraping endpoint
        // (mapped as /metrics in Program.cs). AddMeter subscribes the pipeline to our instruments;
        // nothing else is collected, so no request content can leak into metrics.
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(PaymentMetrics.MeterName)
                .AddPrometheusExporter());

        return services;
    }
}
