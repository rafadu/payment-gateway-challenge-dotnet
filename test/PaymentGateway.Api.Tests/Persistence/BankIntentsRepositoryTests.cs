using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Persistence;

/// <summary>
/// Tests for <see cref="InMemoryBankIntentsRepository"/> — the unit-test fixture backing the
/// <c>BankIntents</c> outbox collection. The Mongo-backed implementation comes later (B4); these
/// tests pin the repository's CONTRACT (CRUD + FindStaleAsync semantics) which the Mongo impl must
/// honor to be a drop-in replacement.
/// </summary>
public class BankIntentsRepositoryTests
{
    private readonly FakeTimeProvider _clock;
    private readonly InMemoryBankIntentsRepository _repo;

    public BankIntentsRepositoryTests()
    {
        _clock = new FakeTimeProvider(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));
        _repo = new InMemoryBankIntentsRepository(_clock);
    }

    private static BankIntentRequest ARequest() => new()
    {
        CardLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100
    };

    private BankIntent APendingIntent(Guid? id = null) =>
        BankIntent.StartPending(id ?? Guid.NewGuid(), "merchant-42", ARequest(), _clock.GetUtcNow().UtcDateTime);

    private BankIntent AnIntent(
        BankIntentStatus status = BankIntentStatus.Reconciled,
        BankPaymentResponse? response = null,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            MerchantId = "merchant-42",
            Request = ARequest(),
            Response = response,
            Status = status,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime
        };

    // --- Add / Get ----------------------------------------------------------

    [Fact]
    public async Task AddAsync_then_GetAsync_returns_the_intent_unchanged()
    {
        var intent = APendingIntent();

        await _repo.AddAsync(intent);
        var fetched = await _repo.GetAsync(intent.Id);

        fetched.Should().BeSameAs(intent);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_id()
    {
        var fetched = await _repo.GetAsync(Guid.NewGuid());

        fetched.Should().BeNull();
    }

    // --- RecordOutcome -----------------------------------------------------

    [Fact]
    public async Task RecordOutcomeAsync_sets_status_to_Authorized_when_the_response_authorizes()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        var response = new BankPaymentResponse { Authorized = true, AuthorizationCode = "auth-1" };
        var later = _clock.GetUtcNow().UtcDateTime.AddSeconds(1);

        await _repo.RecordOutcomeAsync(intent.Id, response, later);
        var fetched = await _repo.GetAsync(intent.Id);

        fetched!.Status.Should().Be(BankIntentStatus.Authorized);
        fetched.Response.Should().BeSameAs(response);
        fetched.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public async Task RecordOutcomeAsync_sets_status_to_Declined_when_the_response_declines()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        var response = new BankPaymentResponse { Authorized = false };

        await _repo.RecordOutcomeAsync(intent.Id, response, _clock.GetUtcNow().UtcDateTime);

        var fetched = await _repo.GetAsync(intent.Id);
        fetched!.Status.Should().Be(BankIntentStatus.Declined);
        fetched.Response.Should().BeSameAs(response);
    }

    [Fact]
    public async Task RecordOutcomeAsync_is_a_no_op_for_an_unknown_id()
    {
        // The repository's contract: outcome recording only mutates an existing intent. A no-op
        // (not throw) is the safer behavior — the handler's flow already treats unknown ids as
        // exceptional, and the reconciler should never pass an id that doesn't exist.
        var response = new BankPaymentResponse { Authorized = true };

        var act = () => _repo.RecordOutcomeAsync(Guid.NewGuid(), response, _clock.GetUtcNow().UtcDateTime);

        await act.Should().NotThrowAsync();
    }

    // --- MarkReconciled ----------------------------------------------------

    [Fact]
    public async Task MarkReconciledAsync_sets_status_to_Reconciled_and_stamps_UpdatedAt()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        // Realistic sequence: outcome recorded first, then reconciled.
        await _repo.RecordOutcomeAsync(
            intent.Id,
            new BankPaymentResponse { Authorized = true },
            _clock.GetUtcNow().UtcDateTime);
        var later = _clock.GetUtcNow().UtcDateTime.AddSeconds(2);

        await _repo.MarkReconciledAsync(intent.Id, later);

        var fetched = await _repo.GetAsync(intent.Id);
        fetched!.Status.Should().Be(BankIntentStatus.Reconciled);
        fetched.UpdatedAt.Should().Be(later);
    }

    // --- IncrementAttempts -------------------------------------------------

    [Fact]
    public async Task IncrementAttemptsAsync_increments_the_attempts_counter()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        var later = _clock.GetUtcNow().UtcDateTime.AddSeconds(1);

        await _repo.IncrementAttemptsAsync(intent.Id, later);
        await _repo.IncrementAttemptsAsync(intent.Id, later);

        var fetched = await _repo.GetAsync(intent.Id);
        fetched!.Attempts.Should().Be(2);
        fetched.UpdatedAt.Should().Be(later);
    }

    // --- FindStaleAsync ----------------------------------------------------

    [Fact]
    public async Task FindStaleAsync_returns_intents_whose_UpdatedAt_is_older_than_the_threshold()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);

        // Advance the clock past the stale threshold.
        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().ContainSingle().Which.Id.Should().Be(intent.Id);
    }

    [Fact]
    public async Task FindStaleAsync_returns_Authorized_intents_awaiting_Payment_materialization()
    {
        // An intent that the bank responded Authorized on but the gateway hasn't yet persisted the
        // Payment for is exactly the kind of stale row the reconciler needs to pick up.
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        await _repo.RecordOutcomeAsync(
            intent.Id,
            new BankPaymentResponse { Authorized = true },
            _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().ContainSingle().Which.Id.Should().Be(intent.Id);
    }

    [Fact]
    public async Task FindStaleAsync_returns_Declined_intents_awaiting_Payment_materialization()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        await _repo.RecordOutcomeAsync(
            intent.Id,
            new BankPaymentResponse { Authorized = false },
            _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().ContainSingle().Which.Id.Should().Be(intent.Id);
    }

    [Fact]
    public async Task FindStaleAsync_excludes_intents_whose_UpdatedAt_is_within_the_threshold()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);

        // Clock advanced only 5 seconds — well within the 1-minute threshold.
        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddSeconds(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().BeEmpty();
    }

    [Fact]
    public async Task FindStaleAsync_excludes_Reconciled_intents()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        await _repo.RecordOutcomeAsync(
            intent.Id,
            new BankPaymentResponse { Authorized = true },
            _clock.GetUtcNow().UtcDateTime);
        await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().BeEmpty();
    }

    [Fact]
    public async Task FindStaleAsync_excludes_Cancelled_intents()
    {
        var intent = APendingIntent();
        await _repo.AddAsync(intent);
        // Cancelled intents (e.g. bank call never reached the bank) are terminal — no retry,
        // no Payment to create. The reconciler never sees them again.
        await _repo.MarkCancelledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Should().BeEmpty();
    }

    // --- DeleteReconciledOlderThanAsync (TTL cleanup) -----------------------

    [Fact]
    public async Task DeleteReconciledOlderThanAsync_removes_reconciled_intents_older_than_the_cutoff()
    {
        var intent = AnIntent(status: BankIntentStatus.Reconciled);
        await _repo.AddAsync(intent);
        await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(31));

        var deleted = await _repo.DeleteReconciledOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(1);
        var stillThere = await _repo.GetAsync(intent.Id);
        stillThere.Should().BeNull();
    }

    [Fact]
    public async Task DeleteReconciledOlderThanAsync_does_not_remove_recent_reconciled_intents()
    {
        var intent = AnIntent(status: BankIntentStatus.Reconciled);
        await _repo.AddAsync(intent);
        await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var deleted = await _repo.DeleteReconciledOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(0);
        var stillThere = await _repo.GetAsync(intent.Id);
        stillThere.Should().NotBeNull();
    }

    [Theory]
    [InlineData(BankIntentStatus.Pending)]
    [InlineData(BankIntentStatus.Authorized)]
    [InlineData(BankIntentStatus.Declined)]
    [InlineData(BankIntentStatus.Cancelled)]
    public async Task DeleteReconciledOlderThanAsync_does_not_remove_non_Reconciled_intents_even_if_old(BankIntentStatus status)
    {
        // The cleanup pass is purely about bounding collection growth — it must not affect
        // intents the reconciler could still act on, or terminal non-Reconciled states (Cancelled
        // is terminal too, just for a different reason).
        var intent = AnIntent(status: status);
        await _repo.AddAsync(intent);
        if (status == BankIntentStatus.Reconciled) await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);
        if (status == BankIntentStatus.Cancelled) await _repo.MarkCancelledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(60));

        var deleted = await _repo.DeleteReconciledOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(0);
        var stillThere = await _repo.GetAsync(intent.Id);
        stillThere.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteReconciledOlderThanAsync_returns_the_count_of_removed_intents()
    {
        // Seed three Reconciled intents, advance the clock by 60 days, then seed a fresh
        // Reconciled + a Pending. Only the three old Reconciled are eligible for deletion.
        for (var i = 0; i < 3; i++)
        {
            var intent = AnIntent(status: BankIntentStatus.Reconciled);
            await _repo.AddAsync(intent);
            await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);
        }

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(60));

        var recent = AnIntent(status: BankIntentStatus.Reconciled);
        await _repo.AddAsync(recent);
        await _repo.MarkReconciledAsync(recent.Id, _clock.GetUtcNow().UtcDateTime);

        var pending = AnIntent(status: BankIntentStatus.Pending);
        await _repo.AddAsync(pending);

        var deleted = await _repo.DeleteReconciledOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(3);
        (await _repo.GetAsync(recent.Id)).Should().NotBeNull("recent Reconciled must not be deleted");
        (await _repo.GetAsync(pending.Id)).Should().NotBeNull("Pending must never be deleted by cleanup");
    }

    // --- DeletePendingOlderThanAsync (TTL for Pending stuck from bank failures) ----

    [Fact]
    public async Task DeletePendingOlderThanAsync_removes_old_Pending_intents_using_CreatedAt()
    {
        // CreatedAt is set when the intent is first written; never mutated by the handler or the
        // sweeper (the sweeper only bumps UpdatedAt + Attempts). The TTL cutoff must use
        // CreatedAt — otherwise the sweeper's per-pass bumps would keep the cutoff moving
        // forward and Pending would never be eligible. This test seeds an old-CreatedAt /
        // recent-UpdatedAt intent and asserts it gets deleted.
        var intent = AnIntent(status: BankIntentStatus.Pending);
        await _repo.AddAsync(intent);  // CreatedAt = now

        // Advance the clock past the retention threshold; that's the intent's age for TTL purposes.
        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(60));

        // The sweeper would have bumped UpdatedAt to "now" — simulate that.
        await _repo.IncrementAttemptsAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        var deleted = await _repo.DeletePendingOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(1);
        (await _repo.GetAsync(intent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeletePendingOlderThanAsync_does_not_remove_recent_Pending_intents()
    {
        var intent = AnIntent(status: BankIntentStatus.Pending);
        await _repo.AddAsync(intent);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var deleted = await _repo.DeletePendingOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(0);
        (await _repo.GetAsync(intent.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(BankIntentStatus.Authorized)]
    [InlineData(BankIntentStatus.Declined)]
    [InlineData(BankIntentStatus.Reconciled)]
    [InlineData(BankIntentStatus.Cancelled)]
    public async Task DeletePendingOlderThanAsync_does_not_remove_non_Pending_intents_even_if_old(BankIntentStatus status)
    {
        // Authorized/Declined mean the reconciler sweeper itself is broken — that's a signal
        // for ops, not dead weight. Reconciled has its own TTL pass. Cancelled is unused today
        // but kept as a defensive terminal. The Pending cleanup pass must NOT touch any of these.
        var intent = AnIntent(status: status);
        await _repo.AddAsync(intent);
        if (status == BankIntentStatus.Reconciled) await _repo.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);
        if (status == BankIntentStatus.Cancelled) await _repo.MarkCancelledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime);

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(60));

        var deleted = await _repo.DeletePendingOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(0);
        (await _repo.GetAsync(intent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeletePendingOlderThanAsync_returns_the_count_of_removed_intents()
    {
        // Seed three old Pending, then advance the clock and seed one fresh Pending. Only the
        // three old ones should be eligible.
        for (var i = 0; i < 3; i++)
        {
            var intent = AnIntent(status: BankIntentStatus.Pending);
            await _repo.AddAsync(intent);
        }

        _clock.SetUtcNow(_clock.GetUtcNow().UtcDateTime.AddDays(60));

        // Sweeper would have bumped the old ones' UpdatedAt — but that doesn't matter, the TTL
        // filters on CreatedAt.
        foreach (var _ in new int[3])
        {
            // (the loop is just to make the sweeper-bump simulation explicit; in practice
            // IncrementAttemptsAsync is fine to call multiple times on the same intent)
        }

        var recent = AnIntent(status: BankIntentStatus.Pending);
        await _repo.AddAsync(recent);

        var deleted = await _repo.DeletePendingOlderThanAsync(_clock.GetUtcNow().UtcDateTime.AddDays(-30));

        deleted.Should().Be(3);
        (await _repo.GetAsync(recent.Id)).Should().NotBeNull("recent Pending must not be deleted");
    }
}
