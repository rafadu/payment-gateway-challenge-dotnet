using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Models.Responses;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Controllers;

// NOTE: still the scaffold's GET-only stub, retyped to PaymentResponse and given a correct
// 404-on-missing so the suite stays green. The real, auth-aware controller is built in stage 7.
[Route("api/[controller]")]
[ApiController]
public class PaymentsController : Controller
{
    private readonly PaymentsRepository _paymentsRepository;

    public PaymentsController(PaymentsRepository paymentsRepository)
    {
        _paymentsRepository = paymentsRepository;
    }

    [HttpGet("{id:guid}")]
    public ActionResult<PaymentResponse> GetPaymentAsync(Guid id)
    {
        var payment = _paymentsRepository.Get(id);

        if (payment is null)
        {
            return NotFound();
        }

        return Ok(payment);
    }
}
