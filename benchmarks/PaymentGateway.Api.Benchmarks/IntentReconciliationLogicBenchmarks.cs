using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Time.Testing;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Benchmarks;

/// <summary>
/// Micro-benchmarks for <see cref="IntentReconciliationLogic"/> — the per-intent decision
/// logic that runs in the hosted reconciler (B4). In-process with an in-memory repo, so this
/// measures the decision logic alone (no Mongo, no hosted-service loop). Run locally only via
/// <c>dotnet run -c Release --project benchmarks/PaymentGateway.Api.Benchmarks</c>.
///
/// <para><b>Why <see cref="IterationSetupAttribute"/>:</b> the reconciler mutates the repo
/// (reconcile-and-write-Payment, mark-Reconciled, increment-Attempts). Without per-iteration
/// cleanup, every invocation of a benchmark adds a fresh <see cref="BankIntent"/> to the
/// in-memory repo, so <c>FindStaleAsync</c> sees an ever-growing set of stale intents whose
/// reconciliation is repeated work. BDN's auto-tuning then piles on warmup iterations trying
/// to find a stable measurement, and the per-iteration time balloons — observed ~60× growth
/// from iteration 1 to 27. <see cref="IterationSetupAttribute"/> resets the clock and
/// rebuilds the repo + logic each iteration so the measurement is per-op, not per-op-times-N.</para>
/// </summary>
[MemoryDiagnoser]
public class IntentReconciliationLogicBenchmarks
{
    private static readonly DateTimeOffset InitialTime = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private IBankIntentsRepository _intents = null!;
    private IPaymentsRepository _payments = null!;
    private InMemoryBankIntentsRepository _realIntents = null!;
    private FakeTimeProvider _clock = null!;
    private IntentReconciliationLogic _logic = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // The clock is created once — the inner state is reset by IterationSetup, but the
        // instance survives so the GC pressure of recreating it doesn't show up in the
        // measurements.
        _clock = new FakeTimeProvider(InitialTime);
    }

    [IterationSetup]
    public void ResetStateForIteration()
    {
        // Reset the clock + rebuild the in-memory repo + rebuild the logic so each iteration
        // starts from the same baseline. The PaymentRepository mock is also recreated so
        // NSubstitute's per-mock call history doesn't accumulate.
        _clock.SetUtcNow(InitialTime);
        _realIntents = new InMemoryBankIntentsRepository(_clock);
        _intents = _realIntents;
        _payments = Substitute.For<IPaymentsRepository>();
        _logic = new IntentReconciliationLogic(_intents, _payments, NullLogger<IntentReconciliationLogic>.Instance, _clock);
    }

    private static BankPaymentResponse AuthorizedResponse() => new()
    {
        Authorized = true,
        AuthorizationCode = "auth-bench"
    };

    private static BankIntentRequest ARequest() => new()
    {
        CardLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100
    };

    [Benchmark(Baseline = true)]
    public async Task Authorized_intent_materializes_Payment_and_marks_reconciled()
    {
        var intent = new BankIntent
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-bench",
            Request = ARequest(),
            Response = AuthorizedResponse(),
            Status = BankIntentStatus.Authorized,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };
        await _realIntents.AddAsync(intent);

        // Advance the clock so the intent is stale enough to be picked up.
        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));
    }

    [Benchmark]
    public async Task Pending_intent_bumps_Attempts_and_does_not_touch_payments()
    {
        var intent = new BankIntent
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-bench",
            Request = ARequest(),
            Response = null,
            Status = BankIntentStatus.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };
        await _realIntents.AddAsync(intent);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));
    }

    [Benchmark]
    public async Task Authorized_intent_with_existing_Payment_marks_Reconciled_idempotent_catchup()
    {
        var intent = new BankIntent
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-bench",
            Request = ARequest(),
            Response = AuthorizedResponse(),
            Status = BankIntentStatus.Authorized,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };
        await _realIntents.AddAsync(intent);
        // Pre-existing Payment with the same id (simulating write #3 succeeded, write #4 didn't).
        _payments.GetAsync(intent.Id, Arg.Any<CancellationToken>())
            .Returns(new Payment
            {
                Id = intent.Id, MerchantId = intent.MerchantId, Status = PaymentStatus.Authorized
            });

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        await _logic.ReconcileStaleAsync(TimeSpan.FromMinutes(1));
    }
}
