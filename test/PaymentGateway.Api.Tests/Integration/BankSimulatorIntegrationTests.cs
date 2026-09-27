using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// Integration tests against the real Mountebank bank simulator (ADR-0001, design.md
/// "Bank integration"). The simulator decides Authorized vs Declined by the **last digit** of
/// <c>card_number</c>: odd → Authorized, even → Declined, <c>0</c> → 503 (see <c>imposters/
/// bank_simulator.ejs</c>). These tests prove the gateway's wire contract lines up with that
/// predicate end-to-end. Skipped when <c>docker-compose up</c> isn't running — see
/// <see cref="IntegrationFixture"/>.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class BankSimulatorIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public BankSimulatorIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Card_ending_in_odd_digit_is_authorized()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/payments", ARequest(cardEndingIn: 1));
        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.Should().NotBeNull();
        body!.Status.Should().Be(PaymentStatus.Authorized);
    }

    [SkippableFact]
    public async Task Card_ending_in_even_digit_is_declined()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/payments", ARequest(cardEndingIn: 2));
        var body = await response.Content.ReadFromJsonAsync<PaymentResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body!.Status.Should().Be(PaymentStatus.Declined);
    }

    [SkippableFact]
    public async Task Card_ending_in_zero_returns_503_bank_unavailable()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/payments", ARequest(cardEndingIn: 0));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private static PostPaymentRequest ARequest(int cardEndingIn) => new()
    {
        // The simulator only inspects the last digit of card_number, so any 14–19-digit card with
        // the right last digit will trigger the predicate we want to test.
        CardNumber = "222240534324800" + cardEndingIn,
        ExpiryMonth = 12,
        ExpiryYear = DateTime.UtcNow.Year + 1,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private static HttpClient NewClient()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var signingKey = factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("Jwt:SigningKey")!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim("sub", "merchant-it") }),
                NotBefore = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(15),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    SecurityAlgorithms.HmacSha256)
            }));
        return client;
    }
}