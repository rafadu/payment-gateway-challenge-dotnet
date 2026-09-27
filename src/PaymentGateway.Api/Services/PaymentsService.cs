using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Orchestrates the payment decision: build the bank request, adjudicate, persist the outcome.
/// It never persists anything unless the bank returned a definitive Authorized/Declined answer —
/// a <see cref="BankUnavailableException"/> or <see cref="InvalidBankRequestException"/> from the
/// bank client propagates before any <see cref="Payment"/> is created or stored.
/// </summary>
public sealed class PaymentsService : IPaymentsService
{
    private readonly IAcquiringBankClient _bankClient;
    private readonly IPaymentsRepository _repository;

    public PaymentsService(IAcquiringBankClient bankClient, IPaymentsRepository repository)
    {
        _bankClient = bankClient;
        _repository = repository;
    }

    public async Task<Payment> ProcessPaymentAsync(PostPaymentRequest request, string merchantId, CancellationToken cancellationToken = default)
    {
        // The request is already validated by FluentValidation in front of the controller, so
        // CardNumber/Currency/Cvv are non-null and CardNumber is 14–19 digits — hence the `!` and
        // the `[^4..]` slice below are safe. The service deliberately does not re-validate.
        var bankRequest = new BankPaymentRequest
        {
            CardNumber = request.CardNumber!,
            ExpiryDate = $"{request.ExpiryMonth:D2}/{request.ExpiryYear:D4}",
            Currency = request.Currency!,
            Amount = request.Amount,
            Cvv = request.Cvv!
        };

        var bankResponse = await _bankClient.ProcessPaymentAsync(bankRequest, cancellationToken);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            Status = bankResponse.Authorized ? PaymentStatus.Authorized : PaymentStatus.Declined,
            CardNumberLastFour = request.CardNumber![^4..],
            ExpiryMonth = request.ExpiryMonth,
            ExpiryYear = request.ExpiryYear,
            Currency = request.Currency!,
            Amount = request.Amount
        };

        await _repository.AddAsync(payment, cancellationToken);

        return payment;
    }
}
