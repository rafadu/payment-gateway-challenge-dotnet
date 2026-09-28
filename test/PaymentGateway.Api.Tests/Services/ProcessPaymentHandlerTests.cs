using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

/// <summary>
/// Tests for <see cref="ProcessPaymentHandler"/> — the inner-most link of the handler decorator
/// chain. Mocks the bank client, the payments repository, and the bank-intents repository (the
/// outbox, ADR-0013); cross-cutting concerns (metrics, audit outcome) live in the decorators and
/// are tested in <see cref="MetricsDecoratorTests"/> and <see cref="AuditOutcomeDecoratorTests"/>.
/// </summary>
public class ProcessPaymentHandlerTests
{
    private readonly IAcquiringBankClient _bank = Substitute.For<IAcquiringBankClient>();
    private readonly IPaymentsRepository _repository = Substitute.For<IPaymentsRepository>();
    private readonly IBankIntentsRepository _intents = Substitute.For<IBankIntentsRepository>();
    private readonly ProcessPaymentHandler _handler;

    public ProcessPaymentHandlerTests()
    {
        _handler = new ProcessPaymentHandler(_bank, _repository, _intents, NullLogger<ProcessPaymentHandler>.Instance);
    }

    private static PostPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private const string TestMerchantId = "merchant-42";

    private void BankResponds(bool authorized) =>
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = authorized, AuthorizationCode = "auth-code" });

    // --- Happy path (writes #1 through #4 all happen) -----------------------

    [Fact]
    public async Task Authorized_bank_response_writes_intent_then_outcome_then_Payment_then_marks_reconciled()
    {
        // The four-write sequence is what gives the outbox its gap-closing property: if any
        // write after write #1 fails, the reconciler (B3) picks up the stale intent. The order
        // matters — write #2 must precede write #3 so the reconciler can rebuild the Payment
        // from the intent's stored response.
        BankResponds(authorized: true);
        var calls = new List<string>();
        _intents.AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => calls.Add("AddAsync"));
        _intents.RecordOutcomeAsync(Arg.Any<Guid>(), Arg.Any<BankPaymentResponse>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => calls.Add("RecordOutcomeAsync"));
        _repository.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => calls.Add("Payment.AddAsync"));
        _intents.MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => calls.Add("MarkReconciledAsync"));

        var result = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.Status.Should().Be(PaymentStatus.Authorized);
        calls.Should().Equal("AddAsync", "RecordOutcomeAsync", "Payment.AddAsync", "MarkReconciledAsync");
    }

    [Fact]
    public async Task Declined_bank_response_is_persisted_with_Declined_status()
    {
        BankResponds(authorized: false);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.Status.Should().Be(PaymentStatus.Declined);
        persisted!.Status.Should().Be(PaymentStatus.Declined);
    }

    [Fact]
    public async Task The_intent_id_matches_the_Payment_id_so_the_reconciler_can_rebuild_either()
    {
        // BankIntent.Id == Payment.Id is the design choice that keeps the outbox + the payments
        // collection in sync from the outside (a retry from the cached Idempotency-Key response,
        // or a sweeper-materialized Payment, all reference the same id).
        BankResponds(authorized: true);
        BankIntent? persistedIntent = null;
        Payment? persistedPayment = null;
        _intents.When(r => r.AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>())).Do(ci => persistedIntent = ci.Arg<BankIntent>());
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persistedPayment = ci.Arg<Payment>());

        var result = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        persistedIntent!.Id.Should().Be(result.Id);
        persistedPayment!.Id.Should().Be(result.Id);
        persistedIntent.Id.Should().Be(persistedPayment.Id);
    }

    [Fact]
    public async Task The_intent_carries_the_sanitized_request_snapshot_never_PAN_or_CVV()
    {
        BankResponds(authorized: true);
        BankIntent? persistedIntent = null;
        _intents.When(r => r.AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>())).Do(ci => persistedIntent = ci.Arg<BankIntent>());

        await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        persistedIntent!.Request.CardLastFour.Should().Be("8877");
        persistedIntent.Request.ExpiryMonth.Should().Be(4);
        persistedIntent.Request.ExpiryYear.Should().Be(2030);
        persistedIntent.Request.Currency.Should().Be("GBP");
        persistedIntent.Request.Amount.Should().Be(100);
        // PCI safety net (also enforced by the ADR-0008 architecture test): the snapshot type
        // exposes no PAN/CVV properties. Behaviorally locked here too.
        persistedIntent.Request.GetType().GetProperties().Select(p => p.Name)
            .Should().NotContain("CardNumber").And.NotContain("Cvv");
    }

    // --- Wire-format contracts (unchanged by the outbox) ---------------------

    [Fact]
    public async Task Sends_the_full_card_number_and_MM_yyyy_expiry_to_the_bank()
    {
        BankResponds(authorized: true);
        BankPaymentRequest? sent = null;
        _bank.ProcessPaymentAsync(Arg.Do<BankPaymentRequest>(r => sent = r), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        sent.Should().NotBeNull();
        sent!.CardNumber.Should().Be("2222405343248877");
        sent.ExpiryDate.Should().Be("04/2030");
        sent.Currency.Should().Be("GBP");
        sent.Amount.Should().Be(100);
        sent.Cvv.Should().Be("123");
    }

    [Theory]
    [InlineData(4, 2030, "04/2030")]    // single-digit month zero-padded
    [InlineData(12, 2030, "12/2030")]   // two-digit month not over-padded; year not truncated to yy
    public async Task Formats_the_expiry_as_MM_yyyy(int month, int year, string expected)
    {
        BankResponds(authorized: true);
        BankPaymentRequest? sent = null;
        _bank.ProcessPaymentAsync(Arg.Do<BankPaymentRequest>(r => sent = r), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        var request = ARequest();
        request.ExpiryMonth = month;
        request.ExpiryYear = year;
        await _handler.ProcessPaymentAsync(request, TestMerchantId);

        sent!.ExpiryDate.Should().Be(expected);
    }

    [Fact]
    public async Task Persists_only_the_last_four_card_digits_never_the_full_pan()
    {
        BankResponds(authorized: true);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.CardNumberLastFour.Should().Be("8877");
        persisted!.CardNumberLastFour.Should().Be("8877");
        result.ExpiryMonth.Should().Be(4);
        result.ExpiryYear.Should().Be(2030);
        result.Currency.Should().Be("GBP");
        result.Amount.Should().Be(100);
    }

    [Fact]
    public async Task Persists_the_caller_merchant_id_on_the_payment()
    {
        BankResponds(authorized: true);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.MerchantId.Should().Be(TestMerchantId);
        persisted!.MerchantId.Should().Be(TestMerchantId);
    }

    [Fact]
    public async Task Assigns_a_new_unique_id_to_each_payment()
    {
        BankResponds(authorized: true);

        var first = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);
        var second = await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        first.Id.Should().NotBeEmpty();
        second.Id.Should().NotBeEmpty();
        first.Id.Should().NotBe(second.Id);
    }

    // --- Crash points (one test per gap the outbox closes) -------------------

    [Theory]
    [MemberData(nameof(BankFailures))]
    public async Task Bank_failure_after_intent_write_leaves_intent_in_Pending_and_no_Payment_persisted(Exception failure)
    {
        // Crash point 1 (write #1 done, bank call fails): the intent is left in Pending with
        // no bank response and no Payment. The reconciler (B3) picks it up as a stale Pending;
        // without bank-side idempotency the safe action is to leave it for ops (R-7).
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        var thrown = await _handler.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(failure);

        await _intents.Received(1).AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>());
        await _intents.DidNotReceive().RecordOutcomeAsync(Arg.Any<Guid>(), Arg.Any<BankPaymentResponse>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _intents.DidNotReceive().MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recording_outcome_failure_leaves_intent_in_Pending_and_no_Payment_persisted()
    {
        // Crash point 2 (write #2 fails): the intent is left in Pending. No Payment is created
        // and no reconciler mark is set. The reconciler will see a stale Pending on next sweep.
        BankResponds(authorized: true);
        _intents.RecordOutcomeAsync(Arg.Any<Guid>(), Arg.Any<BankPaymentResponse>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated outcome-write failure"));

        var thrown = await _handler.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<InvalidOperationException>();

        await _intents.Received(1).AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>());
        await _intents.DidNotReceive().MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PaymentsRepository_failure_after_outcome_leaves_intent_in_Authorized_for_the_reconciler()
    {
        // Crash point 3 (write #3 fails): THE OUTBOX-CLOSED GAP. Bank said Authorized,
        // Mongo write for Payment fails — without the outbox the gateway would 5xx while the
        // bank was already charged. With the outbox: the intent carries the bank's Authorized
        // response, the reconciler picks it up and materializes the Payment, the merchant
        // eventually sees a 201 (via the Idempotency-Key cache replay, ADR-0003/ADR-0012).
        BankResponds(authorized: true);
        _repository.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated mongo write failure"));

        var thrown = await _handler.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<InvalidOperationException>();

        // Writes #1 and #2 happened, #3 failed, #4 didn't.
        await _intents.Received(1).AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>());
        await _intents.Received(1).RecordOutcomeAsync(
            Arg.Any<Guid>(),
            Arg.Is<BankPaymentResponse>(r => r.Authorized == true),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
        await _intents.DidNotReceive().MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkReconciled_failure_leaves_Payment_persisted_and_intent_in_Authorized_for_a_reconciler_resweep()
    {
        // Crash point 4 (write #4 fails): Payment is durable in Mongo, the merchant's 201 was
        // returned. Only the bookkeeping is stale — the reconciler will eventually mark the
        // intent Reconciled. No data loss; eventual consistency on the outbox housekeeping.
        BankResponds(authorized: true);
        _intents.MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated mark-reconciled failure"));

        var thrown = await _handler.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<InvalidOperationException>();

        await _intents.Received(1).AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>());
        await _intents.Received(1).RecordOutcomeAsync(
            Arg.Any<Guid>(),
            Arg.Any<BankPaymentResponse>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<Exception> BankFailures() =>
    [
        new BankUnavailableException("bank down"),
        new InvalidBankRequestException("we sent a bad request")
    ];

    // --- Bank idempotency key forwarding (ADR-0012) -------------------------

    [Fact]
    public async Task Forwards_the_bank_idempotency_key_to_the_bank_client_when_provided()
    {
        string? sentKey = "untouched";
        BankResponds(authorized: true);
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Do<string?>(k => sentKey = k), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId, bankIdempotencyKey: "merchant-attempt-1");

        sentKey.Should().Be("merchant-attempt-1");
    }

    [Fact]
    public async Task Forwards_a_null_bank_idempotency_key_when_none_is_provided()
    {
        // Default param: call sites that don't opt in (no merchant-side Idempotency-Key header) must
        // still get null forwarded — not an empty string, not the merchant's other ID.
        string? sentKey = "untouched";
        BankResponds(authorized: true);
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Do<string?>(k => sentKey = k), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        await _handler.ProcessPaymentAsync(ARequest(), TestMerchantId);

        sentKey.Should().BeNull();
    }
}
