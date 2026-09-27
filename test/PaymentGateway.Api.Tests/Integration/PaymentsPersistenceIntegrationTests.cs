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

using MongoDB.Bson;
using MongoDB.Driver;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// Integration tests for the MongoDB-backed payments repository (replaces the in-memory
/// ConcurrentDictionary). Verifies that POST /api/payments writes a document to the
/// <c>payments</c> collection in the real MongoDB container and that GET /api/payments/{id}
/// retrieves it through the same collection. Skipped when <c>docker-compose up</c> isn't running.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class PaymentsPersistenceIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public PaymentsPersistenceIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_successful_payment_writes_a_document_to_the_payments_collection_retrievable_by_id()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var signingKey = factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("Jwt:SigningKey")!;

        // Mint a JWT directly with the test signing key — the bank's `localhost:8080` is wired in
        // production; the integration fixture only proves the Mongo half is reachable.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim("sub", "merchant-mongo-it") }),
                NotBefore = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(15),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    SecurityAlgorithms.HmacSha256)
            }));

        // POST a payment through the live bank simulator (card ending in 1 → Authorized).
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

        // Query the payments collection directly to confirm the write happened and the shape is
        // what PaymentDocument defines.
        var mongo = factory.Services.GetRequiredService<IMongoClient>();
        var payments = mongo.GetDatabase("payment_gateway").GetCollection<BsonDocument>("payments");

        var document = await payments
            .Find(Builders<BsonDocument>.Filter.Eq("_id", postBody!.Id.ToString()))
            .FirstOrDefaultAsync();
        document.Should().NotBeNull();
        document!["merchantId"].AsString.Should().Be("merchant-mongo-it");
        document["status"].AsString.Should().Be("Authorized");
        document["cardNumberLastFour"].AsString.Should().Be("8871");
        document["currency"].AsString.Should().Be("GBP");
        document["amount"].AsInt32.Should().Be(100);
        document["expiryMonth"].AsInt32.Should().Be(12);
        document.Contains("cvv").Should().BeFalse();
        document.Contains("cardNumber").Should().BeFalse();

        // GET /api/payments/{id} retrieves it (same round-trip through MongoPaymentsRepository).
        var getResponse = await client.GetAsync($"/api/payments/{postBody.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}