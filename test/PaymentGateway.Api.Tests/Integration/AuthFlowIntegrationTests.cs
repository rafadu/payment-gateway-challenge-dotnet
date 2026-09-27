using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// End-to-end integration test of the <c>/api/auth/token</c> → bearer → <c>POST /api/payments</c>
/// → <c>GET /api/payments/{id}</c> flow against the real Mongo-backed credential store (ADR-0010)
/// and the real Mountebank bank simulator. The seeded demo merchant (<c>demo-merchant</c> /
/// <c>demo-secret</c>, BCrypt-hashed in <c>mongo-init/seed-merchants.js</c>) is the credential
/// under test. Skipped when <c>docker-compose up</c> isn't running.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class AuthFlowIntegrationTests
{
    private const string DemoClientId = "demo-merchant";
    private const string DemoClientSecret = "demo-secret";

    private readonly IntegrationFixture _fixture;

    public AuthFlowIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Full_token_to_payment_to_get_round_trip_against_real_Mongo_and_Mountebank()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        // 1. Mint a token via the real /api/auth/token flow — exercises Mongo →
        //    ICredentialCache → BCrypt → JWT issuance.
        var tokenResponse = await client.PostAsJsonAsync("/api/auth/token",
            new { clientId = DemoClientId, clientSecret = DemoClientSecret });
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        tokenBody.Should().NotBeNull();
        tokenBody!.AccessToken.Should().NotBeNullOrWhiteSpace();

        // 2. Use the bearer token to POST a payment (card ending in 1 → Authorized).
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenBody.AccessToken);

        var postResponse = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest
        {
            CardNumber = "2222405343248871",
            ExpiryMonth = 12,
            ExpiryYear = DateTime.UtcNow.Year + 1,
            Currency = "GBP",
            Amount = 100,
            Cvv = "123"
        });
        postResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var postBody = await postResponse.Content.ReadFromJsonAsync<PaymentResponse>();
        postBody.Should().NotBeNull();
        postBody!.Status.Should().Be(PaymentStatus.Authorized);

        // 3. GET the payment by id as the same merchant → 200 (round-trip ownership check).
        var getResponse = await client.GetAsync($"/api/payments/{postBody.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task A_wrong_demo_secret_returns_401_via_real_Mongo()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/token",
            new { clientId = DemoClientId, clientSecret = "wrong-secret" });

        // Uniform 401 for unknown client vs wrong secret (ADR-0010).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}