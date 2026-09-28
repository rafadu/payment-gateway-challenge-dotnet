using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

/// <summary>
/// Tests for <see cref="IntentReconciliationLogic"/> — the pure logic that turns a stale
/// <see cref="BankIntent"/> into either a persisted <see cref="Payment"/> (Authorized/Declined),
/// an attempt-bumped Pending (left for ops), or a Reconciled catch-up. Separated from the
/// <c>BackgroundService</c> wrapper (B4) so it can be unit-tested without a host.
/// </summary>
public class IntentReconciliationLogicTests
{
    private readonly IBankIntentsRepository _intents = Substitute.For<IBankIntentsRepository>();
    private readonly IPaymentsRepository _payments = Substitute.For<IPaymentsRepository>();
    private readonly ILogger<IntentReconciliationLogic> _logger = Substitute.For<ILogger<IntentReconciliationLogic>>();
    private readonly FakeTimeProvider _clock;
    private readonly IntentReconciliationLogic _logic;

    public IntentReconciliationLogicTests()
    {
        _clock = new FakeTimeProvider(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
        _logic = new IntentReconciliationLogic(_intents, _payments, _logger, _clock);
    }

    private static BankIntentRequest ASnapshot() => new()
    {
        CardLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100
    };

    private BankIntent AnIntent(
        Guid? id = null,
        string merchantId = "merchant-42",
        BankIntentStatus status = BankIntentStatus.Authorized,
        BankPaymentResponse? response = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            MerchantId = merchantId,
            Request = ASnapshot(),
            Response = response,
            Status = status,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };

    private void GivenStaleIntents(params BankIntent[] intents)
    {
        _intents.FindStaleAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(intents);
    }

    // --- The four scenarios the plan called out ------------------------------

    [Fact]
    public async Task Stale_Authorized_intent_with_no_existing_Payment_creates_the_Payment_and_marks_Reconciled()
    {
        // The PRIMARY gap-closing scenario: bank authorized, gateway crashed before write #3.
        // Reconciler rebuilds the Payment from the intent's stored response and closes the intent.
        var intent = AnIntent(response: new BankPaymentResponse { Authorized = true, AuthorizationCode = "auth" });
        GivenStaleIntents(intent);
        _payments.GetAsync(intent.Id, Arg.Any<CancellationToken>()).Returns((Payment?)null);

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        count.Should().Be(1);
        await _payments.Received(1).AddAsync(
            Arg.Is<Payment>(p => p.Id == intent.Id
                              && p.MerchantId == "merchant-42"
                              && p.Status == PaymentStatus.Authorized
                              && p.CardNumberLastFour == "8877"
                              && p.Amount == 100),
            Arg.Any<CancellationToken>());
        await _intents.Received(1).MarkReconciledAsync(intent.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_Declined_intent_with_no_existing_Payment_creates_the_Payment_and_marks_Reconciled()
    {
        var intent = AnIntent(response: new BankPaymentResponse { Authorized = false });
        GivenStaleIntents(intent);
        _payments.GetAsync(intent.Id, Arg.Any<CancellationToken>()).Returns((Payment?)null);

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        count.Should().Be(1);
        await _payments.Received(1).AddAsync(
            Arg.Is<Payment>(p => p.Status == PaymentStatus.Declined),
            Arg.Any<CancellationToken>());
        await _intents.Received(1).MarkReconciledAsync(intent.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_Pending_intent_increments_Attempts_and_does_not_touch_PaymentsRepository()
    {
        // R-7: don't retry the bank call for a stale Pending — unsafe without bank-side idempotency.
        // The Attempts counter increments so ops dashboards can spot a stuck Pending.
        var intent = AnIntent(status: BankIntentStatus.Pending, response: null);
        GivenStaleIntents(intent);
        _payments.GetAsync(intent.Id, Arg.Any<CancellationToken>()).Returns((Payment?)null);

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        count.Should().Be(0);
        await _payments.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _intents.Received(1).IncrementAttemptsAsync(intent.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _intents.DidNotReceive().MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_Authorized_intent_with_existing_Payment_marks_Reconciled_without_creating_a_second_Payment()
    {
        // Crash point 4 (write #4 failed): Payment is durable, intent is still Authorized. The
        // reconciler must NOT create a duplicate Payment — just close the intent.
        var intent = AnIntent(response: new BankPaymentResponse { Authorized = true });
        GivenStaleIntents(intent);
        _payments.GetAsync(intent.Id, Arg.Any<CancellationToken>())
            .Returns(new Payment { Id = intent.Id, MerchantId = "merchant-42", Status = PaymentStatus.Authorized });

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        count.Should().Be(1);
        await _payments.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _intents.Received(1).MarkReconciledAsync(intent.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // --- Resilience + counting -----------------------------------------------

    [Fact]
    public async Task ReconcileStaleAsync_continues_to_the_next_intent_when_one_reconciliation_throws()
    {
        // A single bad intent (Mongo write fails, etc.) must not abort the whole pass — the
        // sweeper is the only thing that retries, so aborting on the first error means one bad
        // intent blocks every other intent behind it.
        var good = AnIntent(response: new BankPaymentResponse { Authorized = true });
        var bad = AnIntent(response: new BankPaymentResponse { Authorized = true });
        GivenStaleIntents(bad, good);
        _payments.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Payment?)null);
        _payments.AddAsync(Arg.Is<Payment>(p => p.Id == bad.Id), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated mongo failure"));

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        // Only the good intent was reconciled; the bad one failed but didn't block the rest.
        count.Should().Be(1);
        await _payments.Received(1).AddAsync(
            Arg.Is<Payment>(p => p.Id == good.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileStaleAsync_returns_zero_when_no_intents_are_stale()
    {
        GivenStaleIntents(/* no stale intents */);

        var count = await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));

        count.Should().Be(0);
        await _payments.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }
}
