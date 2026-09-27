using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Sends a payment to the acquiring bank and returns its adjudication.
/// </summary>
public interface IAcquiringBankClient
{
    /// <summary>
    /// Posts <paramref name="request"/> to the bank and returns its response.
    /// </summary>
    /// <exception cref="BankUnavailableException">
    /// The bank returned a non-success status (other than <c>400</c>), timed out, could not be
    /// reached, or returned an unreadable body — i.e. no definitive authorized/declined answer was
    /// obtained.
    /// </exception>
    /// <exception cref="InvalidBankRequestException">
    /// The bank returned <c>400 Bad Request</c>, meaning the request we sent was missing a required
    /// field — a gateway-side defect, not a bank availability problem.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled by the caller.
    /// </exception>
    Task<BankPaymentResponse> ProcessPaymentAsync(BankPaymentRequest request, CancellationToken cancellationToken = default);
}
