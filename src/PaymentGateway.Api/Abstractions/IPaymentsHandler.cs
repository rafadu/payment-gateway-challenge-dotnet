using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Orchestrates processing a payment: maps an already-validated request to the bank's wire format,
/// asks the acquiring bank to adjudicate it, and persists the outcome. Only bank-adjudicated
/// outcomes (Authorized / Declined) are ever created or stored.
/// </summary>
/// <remarks>
/// Cross-cutting concerns — the <c>payments.processed.count</c> metric and the audit-outcome
/// side-channel — are composed onto this interface as decorators (one for the metric, one for the
/// audit-outcome key). The controller depends only on this abstraction; the concrete handler is
/// the inner-most link of the DI-composed chain (see <c>Program.cs</c>).
/// </remarks>
public interface IPaymentsHandler
{
    /// <summary>
    /// Processes <paramref name="request"/> (assumed already validated) against the acquiring bank
    /// on behalf of <paramref name="merchantId"/>, and persists the resulting <see cref="Payment"/>
    /// tagged with that merchant id (ADR-0010). When <paramref name="bankIdempotencyKey"/> is
    /// non-blank, the handler forwards it to the bank as the <c>Idempotency-Key</c> header so a
    /// retry under the same key replays the bank's previous answer (ADR-0012). Pass <c>null</c>
    /// when the merchant didn't opt in.
    /// </summary>
    /// <returns>The persisted payment, carrying its generated id, bank-adjudicated status, and the caller's merchant id.</returns>
    /// <exception cref="BankUnavailableException">
    /// The bank gave no definitive answer; nothing is persisted.
    /// </exception>
    /// <exception cref="InvalidBankRequestException">
    /// The bank rejected the request as malformed; nothing is persisted.
    /// </exception>
    Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request,
        string merchantId,
        string? bankIdempotencyKey = null,
        CancellationToken cancellationToken = default);
}
