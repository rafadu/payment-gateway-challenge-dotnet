using Microsoft.AspNetCore.Http;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Decorator on <see cref="IPaymentsHandler"/> that stamps the audit-outcome key
/// (<see cref="AuditConventions.OutcomeItemKey"/>) on the active <see cref="HttpContext"/> with
/// the adjudicated <see cref="PaymentStatus"/> name. The audit middleware reads this to
/// distinguish Authorized vs Declined (both are 201s — the status code alone can't tell them
/// apart).
///
/// If no <see cref="HttpContext"/> is active (e.g. background work, unit-test wiring), the
/// decorator skips stamping rather than throwing — the audit middleware will fall back to
/// deriving an outcome from the response status code.
/// </summary>
public sealed class AuditOutcomeDecorator : IPaymentsHandler
{
    private readonly IPaymentsHandler _inner;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditOutcomeDecorator(IPaymentsHandler inner, IHttpContextAccessor httpContextAccessor)
    {
        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request,
        string merchantId,
        CancellationToken cancellationToken = default)
    {
        var payment = await _inner.ProcessPaymentAsync(request, merchantId, cancellationToken);

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            httpContext.Items[AuditConventions.OutcomeItemKey] = payment.Status.ToString();
        }

        return payment;
    }
}
