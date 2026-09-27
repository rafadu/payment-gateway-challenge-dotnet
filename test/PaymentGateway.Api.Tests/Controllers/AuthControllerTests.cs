using System.Net;
using System.Net.Http.Json;
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
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Controllers;

public class AuthControllerTests
{
    private static readonly string DemoSecretHash = BCrypt.Net.BCrypt.HashPassword("s3cret");

    // Fake the Mongo-backed store so the real cache + token service run without any database.
    private static ICredentialStore StoreWithDemoMerchant()
    {
        var store = Substitute.For<ICredentialStore>();
        store.FindByClientIdAsync("demo-merchant", Arg.Any<CancellationToken>())
            .Returns(new MerchantCredential("merchant-42", "demo-merchant", DemoSecretHash));
        return store;
    }

    private static WebApplicationFactory<AuthController> FactoryWith(ICredentialStore store) =>
        new WebApplicationFactory<AuthController>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICredentialStore>();
                services.AddSingleton(store);
                // Audit middleware would otherwise try to write to MongoDB on every request —
                // no Mongo in unit tests.
                services.RemoveAll<IAuditStore>();
                services.AddSingleton<IAuditStore>(new InMemoryAuditStore());
            }));

    private static HttpClient ClientWith(ICredentialStore store) => FactoryWith(store).CreateClient();

    [Fact]
    public async Task Valid_credentials_return_200_with_a_bearer_token_for_the_merchant()
    {
        using var factory = FactoryWith(StoreWithDemoMerchant());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/token",
            new { clientId = "demo-merchant", clientSecret = "s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
        body.ExpiresIn.Should().Be(900);

        // End-to-end: the token minted through the real DI/config chain is signature-valid (against
        // the key the app actually used) and carries the merchant id as sub.
        var signingKey = factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("Jwt:SigningKey")!;
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(body.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = ["HS256"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
        });

        validation.IsValid.Should().BeTrue();
        validation.Claims["sub"].Should().Be("merchant-42");
    }

    [Fact]
    public async Task A_wrong_secret_returns_401()
    {
        var client = ClientWith(StoreWithDemoMerchant());

        var response = await client.PostAsJsonAsync("/api/auth/token",
            new { clientId = "demo-merchant", clientSecret = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_client_returns_401()
    {
        // Store returns null for any clientId it wasn't told about.
        var client = ClientWith(Substitute.For<ICredentialStore>());

        var response = await client.PostAsJsonAsync("/api/auth/token",
            new { clientId = "nobody", clientSecret = "s3cret" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(null, "s3cret")]
    [InlineData("", "s3cret")]
    [InlineData("   ", "s3cret")]
    [InlineData("demo-merchant", null)]
    [InlineData("demo-merchant", "")]
    public async Task Missing_or_blank_credentials_return_400(string? clientId, string? clientSecret)
    {
        var client = ClientWith(StoreWithDemoMerchant());

        var response = await client.PostAsJsonAsync("/api/auth/token", new { clientId, clientSecret });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
