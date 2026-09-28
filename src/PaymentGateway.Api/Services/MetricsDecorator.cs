using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Decorator on <see cref="IPaymentsHandler"/> that records <c>payments.processed.count</c>
/// (ADR-0007) after the inner handler returns a bank-adjudicated payment, tagged by the
/// adjudicated status and currency. A bank failure propagates from the inner handler untouched —
/// no metric is recorded, mirroring the pre-decorator behaviour where the metric only fired on
/// the success path.
/// </summary>
public sealed class MetricsDecorator : IPaymentsHandler
{
    private readonly IPaymentsHandler _inner;
    private readonly PaymentMetrics _metrics;

    public MetricsDecorator(IPaymentsHandler inner, PaymentMetrics metrics)
    {
        _inner = inner;
        _metrics = metrics;
    }

    public async Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request,
        string merchantId,
        CancellationToken cancellationToken = default)
    {
        var payment = await _inner.ProcessPaymentAsync(request, merchantId, cancellationToken);
        _metrics.RecordProcessed(payment.Status, payment.Currency);
        return payment;
    }
}
