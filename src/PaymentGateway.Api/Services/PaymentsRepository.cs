using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Services;

// NOTE: still the scaffold's List<T>-backed shape, only retyped to PaymentResponse to keep the
// build green. It is replaced by a thread-safe IPaymentsRepository in stage 2.
public class PaymentsRepository
{
    public List<PaymentResponse> Payments = new();

    public void Add(PaymentResponse payment)
    {
        Payments.Add(payment);
    }

    public PaymentResponse? Get(Guid id)
    {
        return Payments.FirstOrDefault(p => p.Id == id);
    }
}
