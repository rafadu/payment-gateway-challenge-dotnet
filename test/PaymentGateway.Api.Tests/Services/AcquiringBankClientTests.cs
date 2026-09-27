using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PaymentGateway.Api.Clients;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Tests.Services;

public class AcquiringBankClientTests
{
    private readonly IMeterFactory _meterFactory;
    private readonly PaymentMetrics _metrics;

    public AcquiringBankClientTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        _meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        _metrics = new PaymentMetrics(_meterFactory);
    }

    private static BankPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryDate = "04/2025",
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private AcquiringBankClient ClientFor(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://bank.test") };
        if (timeout is not null)
        {
            httpClient.Timeout = timeout.Value;
        }

        return new AcquiringBankClient(httpClient, _metrics);
    }

    private MetricCollector<double> BankCallCollector() =>
        new(_meterFactory, PaymentMetrics.MeterName, "payments.bank.call.duration");

    private static StubHttpMessageHandler RespondsWith(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    [Fact]
    public async Task Returns_the_authorized_result_from_a_200_response()
    {
        var client = ClientFor(RespondsWith(HttpStatusCode.OK,
            """{ "authorized": true, "authorization_code": "0bb07405-6d44-4b50-a14f-7ae0beff13ad" }"""));

        var result = await client.ProcessPaymentAsync(ARequest());

        result.Authorized.Should().BeTrue();
        result.AuthorizationCode.Should().Be("0bb07405-6d44-4b50-a14f-7ae0beff13ad");
    }

    [Fact]
    public async Task Returns_the_declined_result_from_a_200_response()
    {
        var client = ClientFor(RespondsWith(HttpStatusCode.OK,
            """{ "authorized": false, "authorization_code": "" }"""));

        var result = await client.ProcessPaymentAsync(ARequest());

        result.Authorized.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Throws_BankUnavailable_on_a_non_success_status(HttpStatusCode status)
    {
        var client = ClientFor(RespondsWith(status, "{}"));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();
    }

    [Fact]
    public async Task Throws_InvalidBankRequest_on_a_400_rather_than_treating_it_as_unavailable()
    {
        // A 400 means the gateway sent a request missing a required field — a gateway-side defect,
        // not the bank being unavailable, so it must NOT surface as BankUnavailableException.
        var client = ClientFor(RespondsWith(HttpStatusCode.BadRequest,
            """{ "error_message": "Not all required properties were sent in the request" }"""));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<InvalidBankRequestException>();
    }

    [Fact]
    public async Task Throws_BankUnavailable_when_the_call_times_out()
    {
        // The handler never completes; the HttpClient's short timeout cancels it, which the client
        // must translate into BankUnavailableException (not surface as a bare cancellation).
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = ClientFor(handler, timeout: TimeSpan.FromMilliseconds(50));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();
    }

    [Theory]
    [InlineData("null")]              // 200 with a literal null body
    [InlineData("{ not json ")]       // 200 with malformed JSON
    [InlineData("<html>oops</html>")] // 200 whose body isn't JSON at all (still sent as application/json)
    public async Task Throws_BankUnavailable_when_a_200_body_cannot_be_interpreted(string body)
    {
        var client = ClientFor(RespondsWith(HttpStatusCode.OK, body));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();
    }

    [Fact]
    public async Task Throws_BankUnavailable_when_a_200_has_a_non_json_content_type()
    {
        // A real proxy/gateway error page arrives as text/html; ReadFromJsonAsync throws
        // NotSupportedException for the wrong content type — a distinct branch from malformed JSON.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>oops</html>", Encoding.UTF8, "text/html")
        }));
        var client = ClientFor(handler);

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();
    }

    [Fact]
    public async Task Throws_BankUnavailable_when_the_bank_cannot_be_reached()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("Connection refused"));
        var client = ClientFor(handler);

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();
    }

    [Fact]
    public async Task Propagates_a_caller_initiated_cancellation()
    {
        // A cancellation the caller asked for must NOT be masked as "bank unavailable".
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = ClientFor(handler);

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Posts_the_request_to_payments_in_the_banks_snake_case_wire_format()
    {
        var handler = RespondsWith(HttpStatusCode.OK,
            """{ "authorized": true, "authorization_code": "code" }""");
        var client = ClientFor(handler);

        await client.ProcessPaymentAsync(ARequest());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/payments");

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("card_number").GetString().Should().Be("2222405343248877");
        root.GetProperty("expiry_date").GetString().Should().Be("04/2025");
        root.GetProperty("currency").GetString().Should().Be("GBP");
        root.GetProperty("amount").GetInt32().Should().Be(100);
        root.GetProperty("cvv").GetString().Should().Be("123");
    }

    [Fact]
    public async Task Records_bank_call_duration_with_a_success_outcome_on_a_definitive_answer()
    {
        using var collector = BankCallCollector();
        var client = ClientFor(RespondsWith(HttpStatusCode.OK,
            """{ "authorized": true, "authorization_code": "x" }"""));

        await client.ProcessPaymentAsync(ARequest());

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Tags["acquirer"].Should().Be("simulator");
        snapshot[0].Tags["outcome"].Should().Be("success");
        snapshot[0].Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Records_bank_call_duration_with_a_timeout_outcome_when_the_call_times_out()
    {
        using var collector = BankCallCollector();
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = ClientFor(handler, timeout: TimeSpan.FromMilliseconds(50));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Tags["outcome"].Should().Be("timeout");
    }

    [Fact]
    public async Task Records_bank_call_duration_with_an_error_outcome_on_a_non_success_status()
    {
        using var collector = BankCallCollector();
        var client = ClientFor(RespondsWith(HttpStatusCode.ServiceUnavailable, "{}"));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<BankUnavailableException>();

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Tags["outcome"].Should().Be("error");
    }

    [Fact]
    public async Task Records_bank_call_duration_with_an_invalid_request_outcome_on_a_bank_400()
    {
        // A bank 400 is a gateway-side defect, not an availability failure (ADR-0007/R-002), so it
        // must not share the 'error' bucket that feeds circuit-breaker/availability sizing.
        using var collector = BankCallCollector();
        var client = ClientFor(RespondsWith(HttpStatusCode.BadRequest,
            """{ "error_message": "Not all required properties were sent in the request" }"""));

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest()))
            .Should().ThrowAsync<InvalidBankRequestException>();

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Tags["outcome"].Should().Be("invalidrequest");
    }

    [Fact]
    public async Task Does_not_record_a_bank_call_for_a_caller_initiated_cancellation()
    {
        // A genuine caller cancellation is not a bank latency/availability event — it must not
        // pollute the histogram.
        using var collector = BankCallCollector();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = ClientFor(handler);

        await client.Invoking(c => c.ProcessPaymentAsync(ARequest(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        collector.GetMeasurementSnapshot().Should().BeEmpty();
    }
}
