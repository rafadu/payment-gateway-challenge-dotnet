using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PaymentGateway.Api.Tests;

/// <summary>
/// One-shot diagnostic test for the Prometheus /metrics endpoint. If the endpoint fails to
/// register (returns 404), the test surfaces it loudly so the integration walkthrough can
/// spot it. Kept as a real test rather than a throwaway because the /metrics route is part
/// of the public surface documented in ADR-0007.
/// </summary>
public class PrometheusEndpointTests
{
    [Fact]
    public async Task GET_metrics_responds_200_with_a_Prometheus_text_response()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "/metrics is part of the public surface (ADR-0007) — the Prometheus scraping endpoint MUST be reachable for ops dashboards.");
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("text/plain");
    }
}
