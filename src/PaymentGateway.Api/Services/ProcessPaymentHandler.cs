using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Inner-most link of the <see cref="IPaymentsHandler"/> decorator chain: maps an already-validated
/// merchant request to the bank's wire format, asks the bank to adjudicate, then materialises the
/// outcome via <see cref="Payment.FromBankOutcome"/> and persists it. It is intentionally narrow —
/// cross-cutting concerns (metrics, audit outcome) live in decorator implementations that wrap this
/// one via DI.
/// </summary>
public sealed class ProcessPaymentHandler : IPaymentsHandler
{
    private readonly IAcquiringBankClient _bankClient;
    private readonly IPaymentsRepository _repository;

    public ProcessPaymentHandler(IAcquiringBankClient bankClient, IPaymentsRepository repository)
    {
        _bankClient = bankClient;
        _repository = repository;
    }

    public async Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request,
        string merchantId,
        CancellationToken cancellationToken = default)
    {
        var bankRequest = BankPaymentRequest.FromMerchantRequest(request);
        // Slice E1 placeholder: forward null for the idempotency key. E2 will thread the key
        // through IPaymentsHandler so the merchant's header reaches the bank (ADR-0012).
        var bankResponse = await _bankClient.ProcessPaymentAsync(bankRequest, null, cancellationToken);
        var payment = Payment.FromBankOutcome(merchantId, request, bankResponse);
        await _repository.AddAsync(payment, cancellationToken);
        return payment;
    }
}
