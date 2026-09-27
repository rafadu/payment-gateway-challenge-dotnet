using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using NSubstitute;

using PaymentGateway.Api.Controllers;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Controllers;

public class PaymentsControllerTests
{
    private const string MerchantA = "merchant-A";
    private const string MerchantB = "merchant-B";
    private const string WrongSigningKey = "different-signing-key-that-is-also-32-bytes-or-more-yes";

    private static (WebApplicationFactory<PaymentsController> factory, HttpClient client) FactoryWith(IPaymentsRepository repository)
    {
        var factory = new WebApplicationFactory<PaymentsController>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the in-memory repo the API registers as a singleton so each test gets
                // a clean store seeded with whatever payments the test needs.
                var existing = services.Single(d => d.ServiceType == typeof(IPaymentsRepository));
                services.Remove(existing);
                services.AddSingleton(repository);
            });
        });
        return (factory, factory.CreateClient());
    }

    // WAF ConfigureAppConfiguration overrides don't reach config read inside AddX extensions (they
    // run before Build), so the app uses the real appsettings.json signing key. Mint test tokens
    // with that same key.
    private static string SigningKey(WebApplicationFactory<PaymentsController> factory) =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("Jwt:SigningKey")!;

    // POST happy-path needs to bypass the real bank client (localhost:8080 is not running in
    // tests); substitute one that returns a deterministic Authorized/Declined answer.
    private static (WebApplicationFactory<PaymentsController> factory, HttpClient client) FactoryWithBankStub(IPaymentsRepository repository, bool bankAuthorized)
    {
        var bankStub = Substitute.For<IAcquiringBankClient>();
        bankStub.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = bankAuthorized, AuthorizationCode = "auth-code" });

        var factory = new WebApplicationFactory<PaymentsController>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existingRepo = services.Single(d => d.ServiceType == typeof(IPaymentsRepository));
                services.Remove(existingRepo);
                services.AddSingleton(repository);
                services.RemoveAll<IAcquiringBankClient>();
                services.AddSingleton(bankStub);
            });
        });
        return (factory, factory.CreateClient());
    }

    private static void Authorize(WebApplicationFactory<PaymentsController> factory, HttpClient client, string merchantId, TimeSpan? lifetime = null) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwt.Mint(merchantId, SigningKey(factory), lifetime));

    // --- Auth gating ---------------------------------------------------------

    [Fact]
    public async Task GET_returns_401_when_no_token_is_provided()
    {
        var (_, client) = FactoryWith(new PaymentsRepository());

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_returns_401_when_the_token_is_malformed()
    {
        var (_, client) = FactoryWith(new PaymentsRepository());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_returns_401_when_the_token_is_signed_with_the_wrong_key()
    {
        var (_, client) = FactoryWith(new PaymentsRepository());
        var forged = TestJwt.Mint(MerchantA, WrongSigningKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_returns_401_when_the_token_has_expired()
    {
        var (factory, client) = FactoryWith(new PaymentsRepository());
        Authorize(factory, client, MerchantA, lifetime: TimeSpan.FromMinutes(-1));

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_returns_401_when_no_token_is_provided()
    {
        var (_, client) = FactoryWith(new PaymentsRepository());

        var response = await client.PostAsJsonAsync("/api/payments", AValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- POST validation -----------------------------------------------------

    [Fact]
    public async Task POST_returns_400_when_the_request_fails_validation()
    {
        var (factory, client) = FactoryWith(new PaymentsRepository());
        Authorize(factory, client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "123",                  // too short
            ExpiryMonth = 1,
            ExpiryYear = 2025,                  // past, since today is 2026-09-27
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_returns_201_with_the_payment_when_the_request_is_authorized_and_valid()
    {
        var (factory, client) = FactoryWithBankStub(new PaymentsRepository(), bankAuthorized: true);
        Authorize(factory, client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", AValidRequest());
        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.Should().NotBeNull();
        body!.Status.Should().Be(PaymentStatus.Authorized);
        body.CardNumberLastFour.Should().Be("8877");
        body.Currency.Should().Be("GBP");
        body.Amount.Should().Be(100);
        body.Id.Should().NotBeEmpty();

        // CreatedAtAction → the Location header should point at the GET route for this payment.
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith($"/api/Payments/{body.Id}");
    }

    [Fact]
    public async Task POST_returns_201_with_a_declined_payment_when_the_bank_declines()
    {
        var (factory, client) = FactoryWithBankStub(new PaymentsRepository(), bankAuthorized: false);
        Authorize(factory, client, MerchantA);

        var response = await client.PostAsJsonAsync("/api/payments", AValidRequest());
        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body!.Status.Should().Be(PaymentStatus.Declined);
    }

    [Fact]
    public async Task POST_persists_the_payment_under_the_calling_merchants_id_so_GET_returns_200()
    {
        // End-to-end: the POST response carries a payment id; GETting that id as the same merchant
        // returns 200. Cross-merchant 404 (above) is the negative half of the same ownership
        // property — together they prove MerchantId is taken from the JWT sub, not the body.
        var repository = new PaymentsRepository();
        var (factory, client) = FactoryWithBankStub(repository, bankAuthorized: true);
        Authorize(factory, client, MerchantA);

        var postResponse = await client.PostAsJsonAsync("/api/payments", AValidRequest());
        var postBody = await postResponse.Content.ReadFromJsonAsync<PaymentResponse>();

        var getResponse = await client.GetAsync($"/api/payments/{postBody!.Id}");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- GET ownership -------------------------------------------------------

    [Fact]
    public async Task GET_returns_200_with_the_payment_when_the_caller_owns_it()
    {
        var repository = new PaymentsRepository();
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = MerchantA,
            Status = PaymentStatus.Authorized,
            CardNumberLastFour = "8877",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100
        };
        repository.Add(payment);
        var (factory, client) = FactoryWith(repository);
        Authorize(factory, client, MerchantA);

        var response = await client.GetAsync($"/api/payments/{payment.Id}");
        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().BeEquivalentTo(new PaymentResponse
        {
            Id = payment.Id,
            Status = PaymentStatus.Authorized,
            CardNumberLastFour = "8877",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100
        });
    }

    [Fact]
    public async Task GET_returns_404_when_the_payment_does_not_exist()
    {
        var (factory, client) = FactoryWith(new PaymentsRepository());
        Authorize(factory, client, MerchantA);

        var response = await client.GetAsync($"/api/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_returns_404_when_the_caller_does_not_own_the_payment()
    {
        // Repository has a payment owned by MerchantB; MerchantA asks for it. ADR-0010 says 404
        // (not 403) so the existence of another merchant's payment is never revealed.
        var repository = new PaymentsRepository();
        var bobsPayment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = MerchantB,
            Status = PaymentStatus.Authorized,
            CardNumberLastFour = "8877",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100
        };
        repository.Add(bobsPayment);
        var (factory, client) = FactoryWith(repository);
        Authorize(factory, client, MerchantA);

        var response = await client.GetAsync($"/api/payments/{bobsPayment.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static PostPaymentRequest AValidRequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 12,
        ExpiryYear = DateTime.UtcNow.Year + 1,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };
}

internal static class TestJwt
{
    public static string Mint(string subject, string signingKey, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim("sub", subject) }),
            NotBefore = now,
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = creds
        });
        return token;
    }
}