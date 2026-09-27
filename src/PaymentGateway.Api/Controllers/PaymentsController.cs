using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Controllers;

/// <summary>
/// Merchant-facing payment API. Both actions require a valid JWT bearer token (ADR-0010): the
/// caller's merchant id is read from the token's <c>sub</c> claim, set on <see cref="Payment"/>
/// when one is created, and used to scope <c>GET /api/payments/{id}</c> so a merchant can only
/// retrieve its own payments (cross-merchant access returns <c>404</c>, never <c>403</c>).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentsService _paymentsService;
    private readonly IPaymentsRepository _paymentsRepository;
    private readonly IValidator<PostPaymentRequest> _validator;
    private readonly PaymentMetrics _metrics;

    public PaymentsController(
        IPaymentsService paymentsService,
        IPaymentsRepository paymentsRepository,
        IValidator<PostPaymentRequest> validator,
        PaymentMetrics metrics)
    {
        _paymentsService = paymentsService;
        _paymentsRepository = paymentsRepository;
        _validator = validator;
        _metrics = metrics;
    }

    [HttpPost]
    public async Task<IActionResult> ProcessPayment(
        [FromBody] PostPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            // Count the rejection and each failed rule (ADR-0007). Rule names are the bounded set of
            // validated property names; currency is normalised to a real code or "unknown" so a
            // probe sending garbage currencies can't inflate tag cardinality.
            var failedRules = validation.Errors.Select(e => e.PropertyName).Distinct();
            _metrics.RecordRejection(NormaliseCurrency(request.Currency), failedRules);

            var details = new ValidationProblemDetails(validation.ToDictionary())
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Request validation failed."
            };
            return new ObjectResult(details)
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }

        var merchantId = CallerMerchantId();

        try
        {
            var payment = await _paymentsService.ProcessPaymentAsync(request, merchantId, cancellationToken);
            // The audit filter reads this to distinguish Authorized vs Declined (both are 201s).
            HttpContext.Items[AuditMiddleware.OutcomeItemKey] = payment.Status.ToString();
            return CreatedAtAction(nameof(GetPayment), new { id = payment.Id }, ToResponse(payment));
        }
        catch (BankUnavailableException)
        {
            // The bank gave no definitive answer — surface "we don't know" so a merchant can retry
            // without us pretending to have adjudicated the payment (design.md / ADR-0001).
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The acquiring bank is unavailable.");
        }
        catch (InvalidBankRequestException)
        {
            // Bank rejected the request as malformed — this is a gateway-side defect, not a
            // bank-availability problem. 500 surfaces it as an internal error, not a retriable
            // 503 (design.md / ADR-0001).
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The acquiring bank rejected the request as malformed.");
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentResponse>> GetPayment(Guid id, CancellationToken cancellationToken)
    {
        var payment = await _paymentsRepository.GetAsync(id, cancellationToken);

        // 404 (not 403) when the caller isn't the owner: existence of another merchant's payment
        // must never be revealed (ADR-0010).
        if (payment is null || payment.MerchantId != CallerMerchantId())
        {
            return NotFound();
        }

        return Ok(ToResponse(payment));
    }

    // A rejected request's currency is unvalidated input, so it's only used as a metric tag when it
    // looks like a real 3-letter ISO code; anything else becomes "unknown" to bound tag cardinality.
    private static string NormaliseCurrency(string? currency) =>
        currency is { Length: 3 } && currency.All(char.IsAsciiLetter)
            ? currency.ToUpperInvariant()
            : "unknown";

    private string CallerMerchantId() =>
        User.FindFirstValue("sub")
            ?? throw new InvalidOperationException(
                "Authenticated principal is missing the 'sub' claim; the JWT bearer middleware should have rejected this request.");

    private static PaymentResponse ToResponse(Payment payment) => new()
    {
        Id = payment.Id,
        Status = payment.Status,
        CardNumberLastFour = payment.CardNumberLastFour,
        ExpiryMonth = payment.ExpiryMonth,
        ExpiryYear = payment.ExpiryYear,
        Currency = payment.Currency,
        Amount = payment.Amount
    };
}
