using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="BankIntentReconciler"/> hosted wrapper (R-003): the loop is
/// sleep-first, so it does not touch the repository during host warm-up, and it does reconcile once
/// a poll interval elapses. The reconciliation *logic* is covered separately by
/// <see cref="IntentReconciliationLogicTests"/>; these tests only exercise the timer/loop shape via
/// a spy repository that counts <c>FindStaleAsync</c> calls (one per pass).
/// </summary>
public class BankIntentReconcilerTests
{
    [Fact]
    public async Task Does_not_reconcile_before_the_first_poll_interval_elapses()
    {
        // Sleep-first contract: with a long poll interval, an immediate start-then-stop must see
        // zero passes. (A reconcile-first loop would have called FindStaleAsync once before the
        // first delay — this is the assertion that pins R-003.)
        var spy = new CountingBankIntentsRepository();
        var reconciler = BuildReconciler(spy, pollIntervalSeconds: 3600);

        await reconciler.StartAsync(CancellationToken.None);
        // Give ExecuteAsync ample time to start and reach the initial delay.
        await Task.Delay(250);
        await reconciler.StopAsync(CancellationToken.None);

        spy.FindStaleCallCount.Should().Be(0,
            "a sleep-first loop must not run a pass within the (long) first poll interval");
    }

    [Fact]
    public async Task Reconciles_after_a_poll_interval_elapses()
    {
        // The other half: once the interval passes, a reconcile pass does fire. Uses the smallest
        // interval the options allow (1s) and a generous wait so the assertion is bounded, not racy.
        var spy = new CountingBankIntentsRepository();
        var reconciler = BuildReconciler(spy, pollIntervalSeconds: 1);

        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => spy.FindStaleCallCount >= 1, timeout: TimeSpan.FromSeconds(5));
        }
        finally
        {
            await reconciler.StopAsync(CancellationToken.None);
        }

        spy.FindStaleCallCount.Should().BeGreaterThanOrEqualTo(1,
            "the reconciler must run a pass once a poll interval has elapsed");
    }

    private static BankIntentReconciler BuildReconciler(IBankIntentsRepository intents, int pollIntervalSeconds)
    {
        // Minimal DI graph the reconciler resolves IntentReconciliationLogic (and its deps) from.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(intents);
        services.AddSingleton<IPaymentsRepository, InMemoryPaymentsRepository>();
        services.AddSingleton<IntentReconciliationLogic>();
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new BankIntentReconcilerOptions
        {
            PollIntervalSeconds = pollIntervalSeconds,
            StaleAfterSeconds = 30
        });
        return new BankIntentReconciler(provider, options, NullLogger<BankIntentReconciler>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Spy <see cref="IBankIntentsRepository"/> that records how many times <c>FindStaleAsync</c>
    /// was called (one call per reconciler pass) and returns no stale intents, so a pass is a no-op.
    /// </summary>
    private sealed class CountingBankIntentsRepository : IBankIntentsRepository
    {
        private int _findStaleCallCount;
        public int FindStaleCallCount => Volatile.Read(ref _findStaleCallCount);

        public Task<IReadOnlyList<BankIntent>> FindStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _findStaleCallCount);
            return Task.FromResult<IReadOnlyList<BankIntent>>(Array.Empty<BankIntent>());
        }

        public Task EnsureIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAsync(BankIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BankIntent?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BankIntent?>(null);
        public Task RecordOutcomeAsync(Guid id, BankPaymentResponse response, DateTime updatedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkReconciledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkCancelledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task IncrementAttemptsAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> DeleteReconciledOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> DeletePendingOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
