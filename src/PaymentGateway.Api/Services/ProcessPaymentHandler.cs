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
        string? bankIdempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var bankRequest = BankPaymentRequest.FromMerchantRequest(request);
        var bankResponse = await _bankClient.ProcessPaymentAsync(bankRequest, bankIdempotencyKey, cancellationToken);
        var payment = Payment.FromBankOutcome(merchantId, request, bankResponse);
        await _repository.AddAsync(payment, cancellationToken);
        return payment;
    }
}
