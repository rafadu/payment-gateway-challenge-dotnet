using FluentAssertions;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Services;

public class IdempotencyStoreTests
{
    private const string Key = "merchant-attempt-1";
    private const string Hash = "hash-A";
    private const string OtherHash = "hash-B";

    private static CachedResponse ACachedResponse() =>
        new(StatusCode: 201, ContentType: "application/json", Location: "/api/payments/abc", Body: [0x7B, 0x7D]);

    [Fact]
    public void TryClaim_with_a_new_key_returns_NewClaim()
    {
        var store = new InMemoryIdempotencyStore();

        var claim = store.TryClaim(Key, Hash);

        claim.Outcome.Should().Be(IdempotencyClaimOutcome.NewClaim);
        claim.CachedResponse.Should().BeNull();
    }

    [Fact]
    public void TryClaim_with_an_existing_in_progress_key_and_the_same_hash_returns_InProgress()
    {
        var store = new InMemoryIdempotencyStore();
        store.TryClaim(Key, Hash);

        var second = store.TryClaim(Key, Hash);

        second.Outcome.Should().Be(IdempotencyClaimOutcome.InProgress);
        second.CachedResponse.Should().BeNull();
    }

    [Fact]
    public void TryClaim_with_an_existing_completed_key_and_the_same_hash_returns_Completed_with_the_cached_response()
    {
        var store = new InMemoryIdempotencyStore();
        var first = store.TryClaim(Key, Hash);
        var cached = ACachedResponse();
        store.Complete(Key, cached);

        var second = store.TryClaim(Key, Hash);

        second.Outcome.Should().Be(IdempotencyClaimOutcome.Completed);
        second.CachedResponse.Should().Be(cached);
    }

    [Fact]
    public void TryClaim_with_an_existing_key_and_a_different_hash_returns_HashMismatch()
    {
        var store = new InMemoryIdempotencyStore();
        store.TryClaim(Key, Hash);

        var second = store.TryClaim(Key, OtherHash);

        second.Outcome.Should().Be(IdempotencyClaimOutcome.HashMismatch);
        second.CachedResponse.Should().BeNull();
    }

    [Fact]
    public void Complete_transitions_an_in_progress_claim_to_completed_with_the_cached_response()
    {
        var store = new InMemoryIdempotencyStore();
        store.TryClaim(Key, Hash);
        var cached = ACachedResponse();

        store.Complete(Key, cached);

        store.TryClaim(Key, Hash).CachedResponse.Should().Be(cached);
    }

    [Fact]
    public void Release_removes_the_claim_so_a_subsequent_TryClaim_with_the_same_hash_returns_NewClaim()
    {
        var store = new InMemoryIdempotencyStore();
        store.TryClaim(Key, Hash);

        store.Release(Key);

        store.TryClaim(Key, Hash).Outcome.Should().Be(IdempotencyClaimOutcome.NewClaim);
    }

    [Fact]
    public void Complete_called_without_a_prior_in_progress_claim_throws()
    {
        var store = new InMemoryIdempotencyStore();

        var act = () => store.Complete(Key, ACachedResponse());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{Key}*");
    }

    [Fact]
    public void Release_is_a_no_op_when_the_key_was_never_claimed()
    {
        var store = new InMemoryIdempotencyStore();

        var act = () => store.Release(Key);

        act.Should().NotThrow();
    }
}