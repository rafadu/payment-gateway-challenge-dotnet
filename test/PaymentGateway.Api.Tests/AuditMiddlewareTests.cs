using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PaymentGateway.Api.Controllers;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Persistence;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PaymentGateway.Api.Tests;

public class AuditMiddlewareTests
{
    private const string MerchantA = "merchant-A";

    private static WebApplicationFactory<Program> FactoryWith(IAuditStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(store);
                // Payments repository needs an in-memory fake too — the production
                // MongoPaymentsRepository would otherwise hang on a connection that isn't running.
                services.RemoveAll<IPaymentsRepository>();
                services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
            }));

    private static void Authorize(HttpClient client, string merchantId)
    {
        // The signing key lives in appsettings.json; WAF ConfigureAppConfiguration overrides don't
        // reach Add* extensions (they run before Build), so read it back from the factory.
        var factory = new WebApplicationFactory<Program>();
        var signingKey = factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("Jwt:SigningKey")!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", PaymentGateway.Api.Tests.Controllers.TestJwt.Mint(merchantId, signingKey));
    }

    [Fact]
    public async Task Authorized_POST_writes_one_record_with_outcome_Authorized_and_masked_summary()
    {
        var store = new InMemoryAuditStore();
        var fakeBank = Substitute.For<IAcquiringBankClient>();
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Models.Bank.BankPaymentResponse { Authorized = true, AuthorizationCode = "auth-code" });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(store);
                services.RemoveAll<IAcquiringBankClient>();
                services.AddSingleton(fakeBank);
                // Payments repository needs an in-memory fake too — the production
                // MongoPaymentsRepository would otherwise hang on a connection that isn't running.
                services.RemoveAll<IPaymentsRepository>();
                services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
            });
        });
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "2222405343248871",
            ExpiryMonth = 12,
            ExpiryYear = DateTime.UtcNow.Year + 1,
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var record = store.Records.Single();
        record.MerchantId.Should().Be(MerchantA);
        record.Method.Should().Be("POST");
        record.Path.Should().Be("/api/payments");
        record.StatusCode.Should().Be(201);
        record.Outcome.Should().Be("Authorized");
        record.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        record.RequestSummary.Should().NotBeNull();
        record.RequestSummary.Should().NotContainKey("cvv");
        record.RequestSummary.Should().NotContainKey("cardNumber");
        record.RequestSummary!.Should().ContainKey("cardNumberLastFour");
        record.RequestSummary["cardNumberLastFour"].Should().Be("8871");
    }

    [Fact]
    public async Task Declined_POST_writes_outcome_Declined()
    {
        var store = new InMemoryAuditStore();
        // Card ends in 2 → Declined (per imposters/bank_simulator.ejs). Bank client registered
        // for prod is at http://localhost:8080 which isn't running in unit tests, so override
        // with a fake that returns Declined directly.
        var fakeBank = Substitute.For<IAcquiringBankClient>();
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Models.Bank.BankPaymentResponse { Authorized = false });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(store);
                services.RemoveAll<IAcquiringBankClient>();
                services.AddSingleton(fakeBank);
                // Payments repository needs an in-memory fake too — the production
                // MongoPaymentsRepository would otherwise hang on a connection that isn't running.
                services.RemoveAll<IPaymentsRepository>();
                services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
            });
        });
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "2222405343248872",
            ExpiryMonth = 12,
            ExpiryYear = DateTime.UtcNow.Year + 1,
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        store.Records.Single().Outcome.Should().Be("Declined");
    }

    [Fact]
    public async Task Unauthenticated_request_writes_one_record_with_empty_merchant_id_and_outcome_Unauthorized()
    {
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();

        // No Authorization header → 401.
        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var record = store.Records.Single();
        record.MerchantId.Should().BeEmpty();
        record.StatusCode.Should().Be(401);
        record.Outcome.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task Validation_failure_writes_outcome_ValidationRejected()
    {
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "123",
            ExpiryMonth = 1,
            ExpiryYear = 2020,
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        store.Records.Single().Outcome.Should().Be("ValidationRejected");
    }

    [Fact]
    public async Task Bank_unavailable_writes_outcome_BankUnavailable()
    {
        var store = new InMemoryAuditStore();
        var fakeBank = Substitute.For<IAcquiringBankClient>();
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BankUnavailableException("bank down"));

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(store);
                services.RemoveAll<IAcquiringBankClient>();
                services.AddSingleton(fakeBank);
                // Payments repository needs an in-memory fake too — the production
                // MongoPaymentsRepository would otherwise hang on a connection that isn't running.
                services.RemoveAll<IPaymentsRepository>();
                services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
            });
        });
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "2222405343248871",
            ExpiryMonth = 12,
            ExpiryYear = DateTime.UtcNow.Year + 1,
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        store.Records.Single().Outcome.Should().Be("BankUnavailable");
    }

    [Fact]
    public async Task GET_request_writes_one_record_with_no_request_summary()
    {
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        // GET on a non-existent id → 404; the audit record should have no request summary (GET
        // has no body to mask).
        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var record = store.Records.Single();
        record.Method.Should().Be("GET");
        record.RequestSummary.Should().BeNull();
        record.Outcome.Should().Be("NotFound");
    }

    [Fact]
    public async Task Infrastructure_endpoints_like_metrics_are_not_audited()
    {
        // R-001: /metrics is scraped continuously; auditing every scrape pollutes the forensic
        // trail (ADR-0004) and drives constant Mongo writes. It must be skipped by the middleware.
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.Records.Should().BeEmpty();
    }
}