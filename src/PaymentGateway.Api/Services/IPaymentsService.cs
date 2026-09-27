using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Orchestrates processing a payment: maps an already-validated request to the bank's wire format,
/// asks the acquiring bank to adjudicate it, and persists the outcome. Only bank-adjudicated
/// outcomes (Authorized / Declined) are ever created or stored.
/// </summary>
public interface IPaymentsService
{
    /// <summary>
    /// Processes <paramref name="request"/> (assumed already validated) against the acquiring bank
    /// and persists the resulting <see cref="Payment"/>.
    /// </summary>
    /// <returns>The persisted payment, carrying its generated id and bank-adjudicated status.</returns>
    /// <exception cref="BankUnavailableException">
    /// The bank gave no definitive answer; nothing is persisted.
    /// </exception>
    /// <exception cref="InvalidBankRequestException">
    /// The bank rejected the request as malformed; nothing is persisted.
    /// </exception>
    Task<Payment> ProcessPaymentAsync(PostPaymentRequest request, CancellationToken cancellationToken = default);
}
