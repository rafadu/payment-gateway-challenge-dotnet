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

using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// Integration tests for the MongoDB-backed audit trail (ADR-0004 audit half). Verifies that
/// the audit middleware actually writes records to the <c>audit_records</c> collection in the
/// real MongoDB container, with the masked request summary and the expected outcome labels.
/// Skipped when <c>docker-compose up</c> isn't running.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class AuditPersistenceIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public AuditPersistenceIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_successful_payment_writes_a_document_to_the_audit_records_collection_queryable_by_merchantId()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { });
        var client = factory.CreateClient();
        var signingKey = factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("Jwt:SigningKey")!;

        // Mint a JWT directly with the test signing key — Mongo is in the loop for the audit
        // collection write, not the auth flow.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim("sub", "merchant-A") }),
                NotBefore = DateTime.UtcNow,
                Expires = DateTime.UtcNow.AddMinutes(15),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    SecurityAlgorithms.HmacSha256)
            }));

        // POST a real payment through the live bank simulator (fake-bank URL is wired in
        // appsettings.json: http://localhost:8080).
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

        // Query the audit_records collection directly to confirm the write happened and is
        // shaped per the AuditRecord contract.
        var mongo = factory.Services.GetRequiredService<IMongoClient>();
        var audit = mongo.GetDatabase("payment_gateway").GetCollection<BsonDocument>("audit_records");

        var documents = await audit
            .Find(Builders<BsonDocument>.Filter.Eq("merchantId", "merchant-A"))
            .ToListAsync();
        documents.Should().NotBeEmpty();

        var doc = documents.Last();
        doc["merchantId"].AsString.Should().Be("merchant-A");
        doc["method"].AsString.Should().Be("POST");
        doc["path"].AsString.Should().Be("/api/payments");
        doc["statusCode"].AsInt32.Should().Be(201);
        doc["outcome"].AsString.Should().Be("Authorized");
        doc["durationMs"].AsInt64.Should().BeGreaterThanOrEqualTo(0);

        // Request summary: PAN is masked to last four, CVV is absent, currency/amount retained.
        var summary = doc["requestSummary"].AsBsonDocument;
        summary["cardNumberLastFour"].AsString.Should().Be("8871");
        summary["currency"].AsString.Should().Be("GBP");
        summary["amount"].AsInt32.Should().Be(100);
        summary.Contains("cvv").Should().BeFalse();
        summary.Contains("cardNumber").Should().BeFalse();
    }
}