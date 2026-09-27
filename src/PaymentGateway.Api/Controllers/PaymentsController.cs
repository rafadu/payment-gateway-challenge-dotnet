using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

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

    public PaymentsController(
        IPaymentsService paymentsService,
        IPaymentsRepository paymentsRepository,
        IValidator<PostPaymentRequest> validator)
    {
        _paymentsService = paymentsService;
        _paymentsRepository = paymentsRepository;
        _validator = validator;
    }

    [HttpPost]
    public async Task<IActionResult> ProcessPayment(
        [FromBody] PostPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
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
    public ActionResult<PaymentResponse> GetPayment(Guid id)
    {
        var payment = _paymentsRepository.Get(id);

        // 404 (not 403) when the caller isn't the owner: existence of another merchant's payment
        // must never be revealed (ADR-0010).
        if (payment is null || payment.MerchantId != CallerMerchantId())
        {
            return NotFound();
        }

        return Ok(ToResponse(payment));
    }

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
