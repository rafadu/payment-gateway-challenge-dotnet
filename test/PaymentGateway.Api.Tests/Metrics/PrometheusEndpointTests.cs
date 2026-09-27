using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Metrics;

public class PrometheusEndpointTests
{
    // Replace the Mongo-backed audit store so the audit middleware doesn't block on a container
    // that isn't running in tests.
    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(new InMemoryAuditStore());
            }));

    [Fact]
    public async Task Metrics_endpoint_exposes_the_custom_meter_in_prometheus_format()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Record against the app's own PaymentMetrics singleton, then scrape.
        factory.Services.GetRequiredService<PaymentMetrics>()
            .RecordProcessed(PaymentStatus.Authorized, "GBP");

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Prometheus exposition renames dots to underscores; assert the custom metric surfaced.
        body.Should().Contain("payments_processed");
    }
}
