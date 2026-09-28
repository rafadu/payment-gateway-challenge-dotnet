using BenchmarkDotNet.Attributes;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Benchmarks;

/// <summary>
/// Micro-benchmarks for <see cref="ProcessPaymentHandler"/> — the inner-most link of the
/// IPaymentsHandler chain, doing the actual bank call + intent writes + Payment materialize.
/// These are in-process benchmarks with mocked bank + repo, so they measure the orchestrator's
/// own overhead (validation skipped, Mongo skipped, Mountebank skipped). Run locally only via
/// <c>dotnet run -c Release --project benchmarks/PaymentGateway.Api.Benchmarks</c>; the output
/// HTML/markdown reports are committed under <c>benchmarks/results/</c> on demand for regression
/// review. These are intentionally NOT part of CI — CI's perf budget is enforced by the
/// Performance tests in <c>test/PaymentGateway.Api.Tests/Performance/</c>.
/// </summary>
[MemoryDiagnoser]
public class PaymentHandlerBenchmarks
{
    private IAcquiringBankClient _bankAuthorized = null!;
    private IAcquiringBankClient _bankDeclined = null!;
    private IAcquiringBankClient _bankUnavailable = null!;
    private IPaymentsRepository _payments = null!;
    private IBankIntentsRepository _intents = null!;
    private ProcessPaymentHandler _handler = null!;

    private const string MerchantId = "merchant-bench";

    private static readonly PostPaymentRequest Request = new()
    {
        CardNumber = "2222405343248871",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create the bank stubs ONCE — they're stateless w.r.t. call history (NSubstitute
        // records the calls, but the returns we set are constants). The per-iteration reset
        // happens in IterationSetup and replaces the *handler* and the *PaymentRepository* /
        // *IBankIntentsRepository* mocks — those DO accumulate call history, and a fresh
        // instance per iteration keeps the measurement honest.
        _bankAuthorized = Substitute.For<IAcquiringBankClient>();
        _bankAuthorized.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true, AuthorizationCode = "auth-code" });

        _bankDeclined = Substitute.For<IAcquiringBankClient>();
        _bankDeclined.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = false });

        _bankUnavailable = Substitute.For<IAcquiringBankClient>();
        _bankUnavailable.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BankUnavailableException("bank down"));
    }

    [IterationSetup]
    public void ResetMocksAndHandler()
    {
        // Fresh NSubstitute mocks per iteration so call-history doesn't accumulate (and slow
        // down subsequent invocations). Each benchmark re-aims the handler at the bank stub it
        // needs; we just hand it the freshly-reset mocks.
        _payments = Substitute.For<IPaymentsRepository>();
        _payments.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _intents = Substitute.For<IBankIntentsRepository>();
        _intents.AddAsync(Arg.Any<BankIntent>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _intents.RecordOutcomeAsync(Arg.Any<Guid>(), Arg.Any<BankPaymentResponse>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _intents.MarkReconciledAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _handler = new ProcessPaymentHandler(
            _bankAuthorized,
            _payments,
            _intents,
            NullLogger<ProcessPaymentHandler>.Instance);
    }

    [Benchmark(Baseline = true)]
    public Task<Payment> Authorized_payment() =>
        _handler.ProcessPaymentAsync(Request, MerchantId, bankIdempotencyKey: null, CancellationToken.None);

    [Benchmark]
    public Task<Payment> Declined_payment()
    {
        // Re-aim the handler at the declined-bank stub for this measurement only.
        _handler = new ProcessPaymentHandler(
            _bankDeclined, _payments, _intents, NullLogger<ProcessPaymentHandler>.Instance);
        return _handler.ProcessPaymentAsync(Request, MerchantId, bankIdempotencyKey: null, CancellationToken.None);
    }

    [Benchmark]
    public Task Bank_unavailable_503()
    {
        // The exception path matters for latency too — a 503 returns faster than the full
        // 4-write outbox flow because writes #2–#4 are skipped. Useful regression guard against
        // someone accidentally re-trying the bank on BankUnavailableException.
        _handler = new ProcessPaymentHandler(
            _bankUnavailable, _payments, _intents, NullLogger<ProcessPaymentHandler>.Instance);
        return _handler.ProcessPaymentAsync(Request, MerchantId, bankIdempotencyKey: null, CancellationToken.None)
            .ContinueWith(t => { _ = t.Exception; return Task.FromResult(new Payment
            {
                Id = Guid.Empty, MerchantId = MerchantId, Status = PaymentStatus.Declined,
                CardNumberLastFour = "0000", ExpiryMonth = 12, ExpiryYear = 2030,
                Currency = "GBP", Amount = 100
            }); });
    }
}
