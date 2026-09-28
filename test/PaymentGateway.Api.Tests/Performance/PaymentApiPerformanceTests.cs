using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Tests.Integration;

namespace PaymentGateway.Api.Tests.Performance;

/// <summary>
/// End-to-end performance tests for the public surface of the API — POST /api/payments through
/// the live gateway, real bank simulator (Mountebank), and real Mongo. These are the perf budget
/// gates that run in CI: a regression on the happy-path latency fails the build before the
/// change reaches production.
///
/// <para>The tests are deliberately conservative (p99 thresholds of 500ms sequential / 2000ms
/// concurrent on a typical CI runner) — they're not trying to catch every fluctuation, just
/// regressions of 2-3x or more on the hot path. The micro-benchmarks under
/// <c>benchmarks/PaymentGateway.Api.Benchmarks/</c> measure the orchestrator's own cost in
/// isolation (sub-millisecond) and are NOT part of CI.</para>
///
/// <para>Run with <c>dotnet test --filter "FullyQualifiedName~Performance" PaymentGateway.sln</c>
/// (requires <c>docker compose up -d bank_simulator mongo</c> — same as the integration suite).
/// </para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
public class PaymentApiPerformanceTests
{
    private const int RequestCount = 50;

    // CI-generous thresholds. A local run typically lands well below these; the goal is to fail
    // when something regresses by 2-3x, not to detect single-digit-percent jitter.
    private static readonly TimeSpan SequentialP99Threshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SequentialMeanThreshold = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ConcurrentP99Threshold = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan ConcurrentMeanThreshold = TimeSpan.FromMilliseconds(500);

    private readonly IntegrationFixture _fixture;

    public PaymentApiPerformanceTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task POST_payments_sequential_p99_latency_stays_under_threshold()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker compose up (bank_simulator + mongo) is not running.");

        var (client, token, factory) = await NewClientWithTokenAsync();
        using var _ = factory; // keep the WebApplicationFactory alive for the duration of the test

        var latencies = new List<TimeSpan>(RequestCount);
        for (var i = 0; i < RequestCount; i++)
        {
            var sw = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/api/payments", AValidRequest());
            sw.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.Created, because:
                $"request {i} failed; latency so far (ms): {string.Join(", ", latencies.Select(l => l.TotalMilliseconds))}");
            latencies.Add(sw.Elapsed);
        }

        var (p50, p95, p99, mean) = ComputePercentiles(latencies);
        ReportPercentiles("sequential", p50, p95, p99, mean, latencies);

        mean.Should().BeLessThan(SequentialMeanThreshold.TotalMilliseconds,
            because: $"mean latency {mean:F1}ms exceeded CI threshold; the orchestrator or one of its deps regressed");
        p99.Should().BeLessThan(SequentialP99Threshold.TotalMilliseconds,
            because: $"p99 latency {p99:F1}ms exceeded CI threshold; tail-latency regression");
    }

    [SkippableFact]
    public async Task POST_payments_concurrent_p99_latency_stays_under_threshold()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker compose up (bank_simulator + mongo) is not running.");

        var (client, token, factory) = await NewClientWithTokenAsync();
        using var _ = factory;

        // Fan out N requests in parallel, measure each end-to-end. This exercises the bank's
        // concurrency, the gateway's per-request scope creation, and Mongo's connection pool.
        // Mean will be higher than sequential because of contention; p99 captures the slow tail.
        var tasks = Enumerable.Range(0, RequestCount).Select(async i =>
        {
            var sw = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/api/payments", AValidRequest());
            sw.Stop();
            return (i, response.StatusCode, sw.Elapsed);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        var failures = results.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        failures.Should().BeEmpty(because:
            $"under load, {failures.Count}/{RequestCount} requests failed non-201: {string.Join(", ", failures.Select(f => $"req {f.i}={f.StatusCode}"))}");

        var latencies = results.Select(r => r.Item3).ToList();
        var (p50, p95, p99, mean) = ComputePercentiles(latencies);
        ReportPercentiles("concurrent", p50, p95, p99, mean, latencies);

        mean.Should().BeLessThan(ConcurrentMeanThreshold.TotalMilliseconds,
            because: $"mean concurrent latency {mean:F1}ms exceeded CI threshold; queue contention or connection-pool exhaustion");
        p99.Should().BeLessThan(ConcurrentP99Threshold.TotalMilliseconds,
            because: $"p99 concurrent latency {p99:F1}ms exceeded CI threshold; tail-latency regression under load");
    }

    private static PostPaymentRequest AValidRequest() => new()
    {
        CardNumber = "2222405343248871", // ends in 1 → Authorized
        ExpiryMonth = 12,
        ExpiryYear = DateTime.UtcNow.Year + 1,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private static async Task<(HttpClient client, string token, WebApplicationFactory<Program> factory)>
        NewClientWithTokenAsync()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var signingKey = factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("Jwt:SigningKey")!;

        var tokenResponse = await client.PostAsJsonAsync("/api/auth/token", new
        {
            clientId = "demo-merchant",
            clientSecret = "demo-secret"
        });
        tokenResponse.EnsureSuccessStatusCode();
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokenBody!.AccessToken);

        return (client, tokenBody.AccessToken, factory);
    }

    private static (double p50, double p95, double p99, double mean) ComputePercentiles(IReadOnlyList<TimeSpan> latencies)
    {
        var sortedMs = latencies.Select(l => l.TotalMilliseconds).OrderBy(x => x).ToList();
        double Percentile(double p)
        {
            // Nearest-rank: index = ceil(p/100 * N) - 1, clamped.
            var raw = (int)Math.Ceiling(p / 100.0 * sortedMs.Count) - 1;
            var idx = Math.Clamp(raw, 0, sortedMs.Count - 1);
            return sortedMs[idx];
        }
        return (Percentile(50), Percentile(95), Percentile(99), sortedMs.Average());
    }

    private static void ReportPercentiles(
        string label, double p50, double p95, double p99, double mean, IReadOnlyList<TimeSpan> latencies)
    {
        // The test output goes to stdout via xUnit — the perf budget is visible in the CI log
        // even on a passing run, so regressions are easy to spot by eye before the assertion fails.
        Console.WriteLine(
            $"[perf] POST /api/payments {label}: n={latencies.Count} " +
            $"p50={p50:F1}ms p95={p95:F1}ms p99={p99:F1}ms mean={mean:F1}ms");
    }
}
