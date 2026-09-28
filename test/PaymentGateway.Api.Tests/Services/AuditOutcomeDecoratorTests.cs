using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

/// <summary>
/// Tests for <see cref="AuditOutcomeDecorator"/> — wraps an inner <see cref="IPaymentsHandler"/>
/// and stamps the audit-outcome key (<see cref="AuditConventions.OutcomeItemKey"/>) on the active
/// <see cref="HttpContext"/> with the adjudicated <see cref="Payment.Status"/>. Authorized vs
/// Declined are both 201s, so the audit middleware needs this side-channel to distinguish them
/// (ADR-0004).
/// </summary>
public class AuditOutcomeDecoratorTests
{
    private readonly IPaymentsHandler _inner = Substitute.For<IPaymentsHandler>();
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
    private readonly DefaultHttpContext _httpContext = new();
    private readonly AuditOutcomeDecorator _decorator;

    public AuditOutcomeDecoratorTests()
    {
        _httpContextAccessor.HttpContext.Returns(_httpContext);
        _decorator = new AuditOutcomeDecorator(_inner, _httpContextAccessor);
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

    [Fact]
    public async Task Stamps_the_audit_outcome_key_with_Authorized_when_the_payment_is_authorized()
    {
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Payment { Status = PaymentStatus.Authorized });

        await _decorator.ProcessPaymentAsync(ARequest(), "merchant-42");

        _httpContext.Items[AuditConventions.OutcomeItemKey].Should().Be("Authorized");
    }

    [Fact]
    public async Task Stamps_the_audit_outcome_key_with_Declined_when_the_payment_is_declined()
    {
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Payment { Status = PaymentStatus.Declined });

        await _decorator.ProcessPaymentAsync(ARequest(), "merchant-42");

        _httpContext.Items[AuditConventions.OutcomeItemKey].Should().Be("Declined");
    }

    [Fact]
    public async Task Does_not_stamp_the_outcome_key_when_the_inner_handler_throws()
    {
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BankUnavailableException("bank down"));

        await _decorator.Invoking(d => d.ProcessPaymentAsync(ARequest(), "merchant-42"))
            .Should().ThrowAsync<BankUnavailableException>();

        _httpContext.Items.ContainsKey(AuditConventions.OutcomeItemKey).Should().BeFalse();
    }

    [Fact]
    public async Task Forwards_the_inner_handlers_payment_to_the_caller_unchanged()
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-42",
            Status = PaymentStatus.Authorized
        };
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(payment);

        var result = await _decorator.ProcessPaymentAsync(ARequest(), "merchant-42");

        result.Should().BeSameAs(payment);
    }

    [Fact]
    public async Task Does_not_throw_when_there_is_no_active_HttpContext()
    {
        // Outside an HTTP request (background work, unit test wiring) there is no HttpContext to
        // stamp. The audit middleware will derive an outcome from the status code instead — losing
        // some fidelity but never crashing the call.
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        _inner.ProcessPaymentAsync(Arg.Any<PostPaymentRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Payment { Status = PaymentStatus.Authorized });

        await _decorator.Invoking(d => d.ProcessPaymentAsync(ARequest(), "merchant-42"))
            .Should().NotThrowAsync();
    }
}
