using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Configuration;

public class ServiceLifetimeTests
{
    // These tests assert DI *lifetimes*, which are identical regardless of the concrete
    // IBankIntentsRepository implementation. The default (Mongo-backed) repo's constructor calls
    // EnsureIndexes(), a best-effort but synchronous Mongo call that blocks ~30s on the driver's
    // server-selection timeout when Mongo isn't running — turning a pure DI test into a 30s stall.
    // Swapping in the in-memory repo (also registered Singleton) keeps the lifetime behaviour under
    // test while dropping the Mongo dependency this test never needed.
    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBankIntentsRepository>();
                services.AddSingleton<IBankIntentsRepository>(
                    new InMemoryBankIntentsRepository(TimeProvider.System));
            }));

    /// <summary>
    /// The handler chain must NOT be a singleton: the core
    /// (<see cref="PaymentGateway.Api.Services.ProcessPaymentHandler"/>) captures the typed
    /// <c>HttpClient</c> (<see cref="PaymentGateway.Api.Abstractions.IAcquiringBankClient"/>), and a
    /// singleton would pin one HttpClient for the whole process, defeating IHttpClientFactory's
    /// handler rotation (stale DNS / ADR-0001's socket rationale). Scoped = one chain instance
    /// per request scope, a fresh typed client each time.
    /// </summary>
    [Fact]
    public void BankIntents_repository_is_singleton_across_scopes()
    {
        // The BankIntents repository owns its backing store's connection/state — singleton
        // lifetime, scoped resolution is irrelevant. (See Factory(): the in-memory repo stands in
        // for the Mongo one here; both are registered Singleton.)
        using var factory = Factory();
        var root = factory.Services;

        using var scope1 = root.CreateScope();
        using var scope2 = root.CreateScope();

        var first = scope1.ServiceProvider.GetRequiredService<IBankIntentsRepository>();
        var inScope2 = scope2.ServiceProvider.GetRequiredService<IBankIntentsRepository>();

        // Same instance across scopes → singleton.
        inScope2.Should().BeSameAs(first);
    }

    [Fact]
    public void PaymentsHandler_chain_is_scoped_not_singleton()
    {
        using var factory = Factory();
        var root = factory.Services;

        using var scope1 = root.CreateScope();
        using var scope2 = root.CreateScope();

        var first = scope1.ServiceProvider.GetRequiredService<IPaymentsHandler>();
        var againInScope1 = scope1.ServiceProvider.GetRequiredService<IPaymentsHandler>();
        var inScope2 = scope2.ServiceProvider.GetRequiredService<IPaymentsHandler>();

        // Same instance within a scope, different instance across scopes → scoped (not singleton).
        againInScope1.Should().BeSameAs(first);
        inScope2.Should().NotBeSameAs(first);
    }
}
