using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Abstractions;

namespace PaymentGateway.Api.Tests.Configuration;

public class ServiceLifetimeTests
{
    /// <summary>
    /// The handler chain must NOT be a singleton: the core
    /// (<see cref="PaymentGateway.Api.Services.ProcessPaymentHandler"/>) captures the typed
    /// <c>HttpClient</c> (<see cref="PaymentGateway.Api.Abstractions.IAcquiringBankClient"/>), and a
    /// singleton would pin one HttpClient for the whole process, defeating IHttpClientFactory's
    /// handler rotation (stale DNS / ADR-0001's socket rationale). Scoped = one chain instance
    /// per request scope, a fresh typed client each time.
    /// </summary>
    [Fact]
    public void PaymentsHandler_chain_is_scoped_not_singleton()
    {
        using var factory = new WebApplicationFactory<Program>();
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
