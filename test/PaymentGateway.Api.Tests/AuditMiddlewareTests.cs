using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PaymentGateway.Api.Controllers;
using PaymentGateway.Api.Models;
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
    private const string DemoSecret = "demo-secret";
    private static readonly string DemoSecretHash = BCrypt.Net.BCrypt.HashPassword(DemoSecret);

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
                // Bank-intents outbox also needs an in-memory fake — see PaymentsControllerTests
                // for the full rationale (the ProcessPaymentHandler's outbox writes hit Mongo by
                // default, which isn't running in this workflow).
                services.RemoveAll<IBankIntentsRepository>();
                services.AddSingleton<IBankIntentsRepository>(new InMemoryBankIntentsRepository(TimeProvider.System));
                // Fake the Mongo-backed credential store so the token endpoint (used by the
                // auth-not-audited test) resolves the demo merchant without a database — otherwise
                // POST /api/auth/token blocks ~30s on the Mongo server-selection timeout.
                var credentials = Substitute.For<ICredentialStore>();
                credentials.FindByClientIdAsync("demo-merchant", Arg.Any<CancellationToken>())
                    .Returns(new MerchantCredential("merchant-42", "demo-merchant", DemoSecretHash));
                services.RemoveAll<ICredentialStore>();
                services.AddSingleton(credentials);
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
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                // Bank-intents outbox also needs an in-memory fake — see PaymentsControllerTests
                // for the full rationale (the ProcessPaymentHandler's outbox writes hit Mongo by
                // default, which isn't running in this workflow).
                services.RemoveAll<IBankIntentsRepository>();
                services.AddSingleton<IBankIntentsRepository>(new InMemoryBankIntentsRepository(TimeProvider.System));
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
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                // Bank-intents outbox also needs an in-memory fake — see PaymentsControllerTests
                // for the full rationale (the ProcessPaymentHandler's outbox writes hit Mongo by
                // default, which isn't running in this workflow).
                services.RemoveAll<IBankIntentsRepository>();
                services.AddSingleton<IBankIntentsRepository>(new InMemoryBankIntentsRepository(TimeProvider.System));
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
    public async Task Unauthenticated_request_is_not_audited()
    {
        // Positive-list audit scope (only POST /api/payments): a 401 on a GET to /api/payments/{id}
        // is noise — the audit trail is about payment attempts, not retrieval. The middleware must
        // skip the request entirely (no store write).
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        store.Records.Should().BeEmpty();
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
        fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                // Bank-intents outbox also needs an in-memory fake — see PaymentsControllerTests
                // for the full rationale (the ProcessPaymentHandler's outbox writes hit Mongo by
                // default, which isn't running in this workflow).
                services.RemoveAll<IBankIntentsRepository>();
                services.AddSingleton<IBankIntentsRepository>(new InMemoryBankIntentsRepository(TimeProvider.System));
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
    public async Task GET_request_is_not_audited()
    {
        // Positive-list audit scope: GET /api/payments/{id} is out of scope. Reading an
        // already-authorized payment is not a security-review-worthy event — the trail is for
        // payment attempts and their outcomes, not retrievals.
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();
        Authorize(client, MerchantA);

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        store.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Auth_token_request_is_not_audited()
    {
        // The audit scope is POST /api/payments. POST /api/auth/token is out of scope — a merchant
        // logging in is not a payment event, and uniform-401 means it doesn't leak existence either.
        // (FactoryWith fakes the credential store with the demo merchant so the token request returns
        // 200 without Mongo; the test still proves that a *successful* login writes no audit record.)
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/token", new
        {
            clientId = "demo-merchant",
            clientSecret = "demo-secret"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Infrastructure_endpoints_like_metrics_are_not_audited()
    {
        // /metrics is scraped continuously; auditing every scrape pollutes the forensic trail
        // (ADR-0004) and drives constant Mongo writes. With the positive-list scope, /metrics is
        // out of scope by definition.
        var store = new InMemoryAuditStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.Records.Should().BeEmpty();
    }
}