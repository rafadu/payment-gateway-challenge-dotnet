using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
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
///
/// <para><b>Outbox flow (ADR-0013 / §3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>):</b> four writes — the
/// <see cref="BankIntent"/> is inserted before the bank is called (write #1) so a gateway crash
/// between the bank call and the <see cref="Payment"/> persist leaves a durable record the
/// reconciler can pick up. The remaining three writes record the bank's adjudication (write #2),
/// insert the <see cref="Payment"/> (write #3), and mark the intent reconciled (write #4). A
/// crash at any point after write #1 leaves the intent in a state the reconciler can act on;
/// write #4 is the only one that's safe to lose (it's eventual-consistency bookkeeping).</para>
/// </summary>
public sealed class ProcessPaymentHandler : IPaymentsHandler
{
    private readonly IAcquiringBankClient _bankClient;
    private readonly IPaymentsRepository _payments;
    private readonly IBankIntentsRepository _intents;

    public ProcessPaymentHandler(
        IAcquiringBankClient bankClient,
        IPaymentsRepository payments,
        IBankIntentsRepository intents)
    {
        _bankClient = bankClient;
        _payments = payments;
        _intents = intents;
    }

    public async Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request,
        string merchantId,
        string? bankIdempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        // Generate the id upfront so the BankIntent and the eventual Payment share it. The
        // cached Idempotency-Key response (ADR-0003) carries this id; the outbox (this slice) uses
        // it to rebuild the Payment on crash recovery; a sweeper-materialized Payment has the same
        // id as the merchant's first attempt.
        var paymentId = Guid.NewGuid();
        var snapshot = BankIntentRequest.FromMerchantRequest(request);

        // Write #1: durable record that a bank interaction was attempted. The intent's id matches
        // the future Payment.Id; the request snapshot is sanitized (last-four only, never PAN/CVV).
        var intent = BankIntent.StartPending(paymentId, merchantId, snapshot, DateTime.UtcNow);
        await _intents.AddAsync(intent, cancellationToken);

        try
        {
            // Bank call — forward the merchant's Idempotency-Key verbatim (ADR-0012) so the bank
            // can dedupe retries.
            var bankRequest = BankPaymentRequest.FromMerchantRequest(request);
            var bankResponse = await _bankClient.ProcessPaymentAsync(bankRequest, bankIdempotencyKey, cancellationToken);

            // Write #2: record the bank's adjudication on the intent. A failure here leaves the
            // intent in Pending — the reconciler sees a stale Pending on next sweep.
            await _intents.RecordOutcomeAsync(paymentId, bankResponse, DateTime.UtcNow, cancellationToken);

            // Write #3: materialize the Payment (same id as the intent). A failure here is the
            // primary gap the outbox closes: bank authorized, gateway forgot. Reconciler rebuilds
            // the Payment from the intent's stored response.
            var payment = Payment.FromBankOutcome(merchantId, request, bankResponse, paymentId);
            await _payments.AddAsync(payment, cancellationToken);

            // Write #4: close the intent. Safe to lose — the reconciler re-marks eventually.
            await _intents.MarkReconciledAsync(paymentId, DateTime.UtcNow, cancellationToken);

            return payment;
        }
        catch (BankUnavailableException)
        {
            // Intent is left Pending (write #1 only) or Authorized/Declined (writes #1–2). The
            // sweeper picks up Authorized/Declined and materializes the Payment. We propagate the
            // 503 so the merchant can retry; the merchant→gateway Idempotency-Key filter releases
            // the claim so the retry reaches the bank (which then dedupes via ADR-0012).
            throw;
        }
    }
}
