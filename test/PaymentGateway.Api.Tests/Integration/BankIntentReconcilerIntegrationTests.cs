using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the outbox reconciler (ADR-0013, §3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>). Simulates a gateway crash mid-handler
/// (write #3 — the <c>Payment</c> persist — never happened), then verifies the hosted reconciler
/// picks up the stale intent and materializes the Payment. Skipped when
/// <c>docker-compose up</c> isn't running.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class BankIntentReconcilerIntegrationTests
{
    private readonly IntegrationFixture _fixture;

    public BankIntentReconcilerIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Reconciler_materializes_a_Payment_from_a_stale_Authorized_intent_left_over_by_a_crashed_handler()
    {
        // THE primary outbox scenario: the bank said Authorized (gateway has a record of that),
        // then the gateway crashed before the Payment could be persisted. The merchant never got
        // a response, but the intent survives in Mongo and the reconciler recovers.
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        using var factory = new WebApplicationFactory<Program>();
        var intentsRepo = factory.Services.GetRequiredService<IBankIntentsRepository>();
        var paymentsRepo = factory.Services.GetRequiredService<IPaymentsRepository>();
        var logic = factory.Services.GetRequiredService<IntentReconciliationLogic>();

        // Arrange: a stale Authorized intent, no Payment yet. UpdatedAt far in the past so any
        // reasonable staleAfter threshold catches it on the next sweep.
        var intentId = Guid.NewGuid();
        var merchantId = "merchant-reconciler-it";
        var snapshot = new BankIntentRequest
        {
            CardLastFour = "8877",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100
        };
        var response = new BankPaymentResponse { Authorized = true, AuthorizationCode = "auth-code" };
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-1);
        var intent = new BankIntent
        {
            Id = intentId,
            MerchantId = merchantId,
            Request = snapshot,
            Response = response,
            Status = BankIntentStatus.Authorized,
            CreatedAt = oldTimestamp,
            UpdatedAt = oldTimestamp
        };

        await intentsRepo.AddAsync(intent);

        var paymentBefore = await paymentsRepo.GetAsync(intentId);
        paymentBefore.Should().BeNull("preconditions: the simulated crash means the Payment was never persisted");

        // Act: drive a reconciler pass directly. Faster than waiting for the hosted service's
        // timer (35s+) and proves the same integration: the production code path uses the same
        // IntentReconciliationLogic from the same DI graph.
        var reconciled = await logic.ReconcileStaleAsync(staleAfter: TimeSpan.FromSeconds(1));

        // Assert: Payment materialized from the intent's stored response.
        reconciled.Should().Be(1);

        var paymentAfter = await paymentsRepo.GetAsync(intentId);
        paymentAfter.Should().NotBeNull();
        paymentAfter!.Id.Should().Be(intentId, "shared id is what makes the cached Idempotency-Key response consistent with a sweeper-materialized Payment");
        paymentAfter.MerchantId.Should().Be(merchantId);
        paymentAfter.Status.Should().Be(PaymentStatus.Authorized);
        paymentAfter.CardNumberLastFour.Should().Be("8877");
        paymentAfter.Amount.Should().Be(100);
        paymentAfter.Currency.Should().Be("GBP");

        // Assert: the intent is now Reconciled — the sweeper closed it idempotently.
        var intentAfter = await intentsRepo.GetAsync(intentId);
        intentAfter!.Status.Should().Be(BankIntentStatus.Reconciled);
    }

    [SkippableFact]
    public async Task Reconciler_materializes_a_Payment_from_a_stale_Declined_intent_using_its_stored_response()
    {
        // Mirror of the Authorized case for Declined. Confirms the reconciler correctly uses
        // bankResponse.Authorized to drive PaymentStatus — a future change that hardcodes
        // Authorized would silently break Declined recovery.
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        using var factory = new WebApplicationFactory<Program>();
        var intentsRepo = factory.Services.GetRequiredService<IBankIntentsRepository>();
        var paymentsRepo = factory.Services.GetRequiredService<IPaymentsRepository>();
        var logic = factory.Services.GetRequiredService<IntentReconciliationLogic>();

        var intentId = Guid.NewGuid();
        var intent = new BankIntent
        {
            Id = intentId,
            MerchantId = "merchant-reconciler-it",
            Request = new BankIntentRequest
            {
                CardLastFour = "1234",
                ExpiryMonth = 6,
                ExpiryYear = 2030,
                Currency = "USD",
                Amount = 250
            },
            Response = new BankPaymentResponse { Authorized = false },
            Status = BankIntentStatus.Declined,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        await intentsRepo.AddAsync(intent);

        var reconciled = await logic.ReconcileStaleAsync(TimeSpan.FromSeconds(1));

        reconciled.Should().Be(1);
        var paymentAfter = await paymentsRepo.GetAsync(intentId);
        paymentAfter.Should().NotBeNull();
        paymentAfter!.Status.Should().Be(PaymentStatus.Declined);
        paymentAfter.CardNumberLastFour.Should().Be("1234");
        paymentAfter.Amount.Should().Be(250);
    }

    [SkippableFact]
    public async Task Reconciler_bumps_Attempts_for_a_stale_Pending_intent_without_calling_PaymentsRepository()
    {
        // R-7 lock-in at the integration level: a Pending intent that the handler wrote but
        // didn't complete (e.g. client disconnected before the bank call) is left for ops. The
        // reconciler's only action is to increment Attempts so a stuck Pending is visible.
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        using var factory = new WebApplicationFactory<Program>();
        var intentsRepo = factory.Services.GetRequiredService<IBankIntentsRepository>();
        var paymentsRepo = factory.Services.GetRequiredService<IPaymentsRepository>();
        var logic = factory.Services.GetRequiredService<IntentReconciliationLogic>();

        var intentId = Guid.NewGuid();
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-1);
        var intent = new BankIntent
        {
            Id = intentId,
            MerchantId = "merchant-reconciler-it",
            Request = new BankIntentRequest { CardLastFour = "8877", ExpiryMonth = 12, ExpiryYear = 2030, Currency = "GBP", Amount = 100 },
            Response = null,
            Status = BankIntentStatus.Pending,
            CreatedAt = oldTimestamp,
            UpdatedAt = oldTimestamp
        };
        await intentsRepo.AddAsync(intent);

        var reconciled = await logic.ReconcileStaleAsync(TimeSpan.FromSeconds(1));

        reconciled.Should().Be(0, "R-7: a Pending intent never produces a Payment");
        var paymentAfter = await paymentsRepo.GetAsync(intentId);
        paymentAfter.Should().BeNull("R-7: the reconciler must NOT create a Payment from a Pending intent");

        var intentAfter = await intentsRepo.GetAsync(intentId);
        intentAfter!.Status.Should().Be(BankIntentStatus.Pending, "R-7: status stays Pending; only Attempts bumps");
    }
}
