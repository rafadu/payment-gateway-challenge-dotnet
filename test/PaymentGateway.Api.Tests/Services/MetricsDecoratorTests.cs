using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

/// <summary>
/// Tests for <see cref="MetricsDecorator"/> — wraps an inner <see cref="IPaymentsHandler"/> and
/// records the <c>payments.processed.count</c> metric (ADR-0007) after the inner handler returns a
/// bank-adjudicated payment. On bank failure, no metric is recorded.
/// </summary>
public class MetricsDecoratorTests
{
    private readonly IPaymentsHandler _inner = Substitute.For<IPaymentsHandler>();
    private readonly IMeterFactory _meterFactory;
    private readonly MetricsDecorator _decorator;

    public MetricsDecoratorTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        _meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        _decorator = new MetricsDecorator(_inner, new PaymentMetrics(_meterFactory));
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

    [Theory]
    [InlineData(PaymentStatus.Authorized, "Authorized")]
    [InlineData(PaymentStatus.Declined, "Declined")]
    public async Task Records_a_processed_payment_tagged_by_adjudicated_status_and_currency_on_success(
        PaymentStatus status, string expectedStatus)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-42",
            Status = status,
            CardNumberLastFour = "8877",
            ExpiryMonth = 4,
            ExpiryYear = 2030,
            Currency = "GBP",
            Amount = 100
        };
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(payment);
        using var collector = new MetricCollector<long>(_meterFactory, PaymentMetrics.MeterName, "payments.processed.count");

        await _decorator.ProcessPaymentAsync(ARequest(), "merchant-42");

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().Be(1);
        snapshot[0].Tags["status"].Should().Be(expectedStatus);
        snapshot[0].Tags["currency"].Should().Be("GBP");
    }

    [Fact]
    public async Task Does_not_record_a_processed_payment_when_the_inner_handler_throws()
    {
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BankUnavailableException("bank down"));
        using var collector = new MetricCollector<long>(_meterFactory, PaymentMetrics.MeterName, "payments.processed.count");

        await _decorator.Invoking(d => d.ProcessPaymentAsync(ARequest(), "merchant-42"))
            .Should().ThrowAsync<BankUnavailableException>();

        collector.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Forwards_the_inner_handlers_payment_to_the_caller_unchanged()
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-42",
            Status = PaymentStatus.Authorized,
            Currency = "GBP"
        };
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(payment);

        var result = await _decorator.ProcessPaymentAsync(ARequest(), "merchant-42");

        result.Should().BeSameAs(payment);
    }

    [Fact]
    public async Task Propagates_the_inner_handlers_exception_unchanged()
    {
        var failure = new InvalidBankRequestException("we sent a bad request");
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        var thrown = await _decorator.Invoking(d => d.ProcessPaymentAsync(ARequest(), "merchant-42"))
            .Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(failure);
    }
}
