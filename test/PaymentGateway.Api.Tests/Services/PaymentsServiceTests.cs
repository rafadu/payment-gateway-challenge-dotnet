using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

public class PaymentsServiceTests
{
    private readonly IAcquiringBankClient _bank = Substitute.For<IAcquiringBankClient>();
    private readonly IPaymentsRepository _repository = Substitute.For<IPaymentsRepository>();
    private readonly IMeterFactory _meterFactory;
    private readonly PaymentsService _service;

    public PaymentsServiceTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        _meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        _service = new PaymentsService(_bank, _repository, new PaymentMetrics(_meterFactory));
    }

    private static PostPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = 2025,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private const string TestMerchantId = "merchant-42";

    private void BankResponds(bool authorized) =>
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = authorized, AuthorizationCode = "auth-code" });

    [Fact]
    public async Task Authorized_bank_response_is_persisted_with_Authorized_status()
    {
        BankResponds(authorized: true);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.Status.Should().Be(PaymentStatus.Authorized);
        _repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        persisted.Should().BeSameAs(result);
    }

    [Fact]
    public async Task Declined_bank_response_is_persisted_with_Declined_status()
    {
        BankResponds(authorized: false);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.Status.Should().Be(PaymentStatus.Declined);
        persisted!.Status.Should().Be(PaymentStatus.Declined);
    }

    [Fact]
    public async Task Sends_the_full_card_number_and_MM_yyyy_expiry_to_the_bank()
    {
        BankResponds(authorized: true);
        BankPaymentRequest? sent = null;
        _bank.ProcessPaymentAsync(Arg.Do<BankPaymentRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        sent.Should().NotBeNull();
        sent!.CardNumber.Should().Be("2222405343248877");
        sent.ExpiryDate.Should().Be("04/2025");
        sent.Currency.Should().Be("GBP");
        sent.Amount.Should().Be(100);
        sent.Cvv.Should().Be("123");
    }

    [Theory]
    [InlineData(4, 2025, "04/2025")]   // single-digit month zero-padded
    [InlineData(12, 2025, "12/2025")]  // two-digit month not over-padded; year not truncated to yy
    public async Task Formats_the_expiry_as_MM_yyyy(int month, int year, string expected)
    {
        BankResponds(authorized: true);
        BankPaymentRequest? sent = null;
        _bank.ProcessPaymentAsync(Arg.Do<BankPaymentRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new BankPaymentResponse { Authorized = true });

        var request = ARequest();
        request.ExpiryMonth = month;
        request.ExpiryYear = year;
        await _service.ProcessPaymentAsync(request, TestMerchantId);

        sent!.ExpiryDate.Should().Be(expected);
    }

    [Fact]
    public async Task Persists_only_the_last_four_card_digits_never_the_full_pan()
    {
        BankResponds(authorized: true);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.CardNumberLastFour.Should().Be("8877");
        persisted!.CardNumberLastFour.Should().Be("8877");
        result.ExpiryMonth.Should().Be(4);
        result.ExpiryYear.Should().Be(2025);
        result.Currency.Should().Be("GBP");
        result.Amount.Should().Be(100);
    }

    [Fact]
    public async Task Persists_the_caller_merchant_id_on_the_payment()
    {
        BankResponds(authorized: true);
        Payment? persisted = null;
        _repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(ci => persisted = ci.Arg<Payment>());

        var result = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        result.MerchantId.Should().Be(TestMerchantId);
        persisted!.MerchantId.Should().Be(TestMerchantId);
    }

    [Fact]
    public async Task Assigns_a_new_unique_id_to_each_payment()
    {
        BankResponds(authorized: true);

        var first = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);
        var second = await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        first.Id.Should().NotBeEmpty();
        second.Id.Should().NotBeEmpty();
        first.Id.Should().NotBe(second.Id);
    }

    [Theory]
    [MemberData(nameof(BankFailures))]
    public async Task A_bank_failure_propagates_and_nothing_is_persisted(Exception failure)
    {
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        var thrown = await _service.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<Exception>();
        // The exact instance must propagate untouched — not swallowed or rewrapped.
        thrown.Which.Should().BeSameAs(failure);

        _repository.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<Exception> BankFailures() =>
    [
        new BankUnavailableException("bank down"),
        new InvalidBankRequestException("we sent a bad request")
    ];

    [Theory]
    [InlineData(true, "Authorized")]
    [InlineData(false, "Declined")]
    public async Task Records_a_processed_payment_tagged_by_adjudicated_status_and_currency(bool authorized, string expectedStatus)
    {
        BankResponds(authorized);
        using var collector = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.processed.count");

        await _service.ProcessPaymentAsync(ARequest(), TestMerchantId);

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().Be(1);
        snapshot[0].Tags["status"].Should().Be(expectedStatus);
        snapshot[0].Tags["currency"].Should().Be("GBP");
    }

    [Fact]
    public async Task Does_not_record_a_processed_payment_when_the_bank_fails()
    {
        _bank.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BankUnavailableException("bank down"));
        using var collector = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.processed.count");

        await _service.Invoking(s => s.ProcessPaymentAsync(ARequest(), TestMerchantId))
            .Should().ThrowAsync<BankUnavailableException>();

        collector.GetMeasurementSnapshot().Should().BeEmpty();
    }
}
