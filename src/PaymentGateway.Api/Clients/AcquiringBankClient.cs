using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Clients;

/// <summary>
/// Typed <see cref="HttpClient"/> adapter over the acquiring bank simulator. The base address and
/// the bounded timeout (ADR-0001) are configured on the injected <see cref="HttpClient"/> at
/// registration, so this class owns only the wire call and the failure-to-<see cref="BankUnavailableException"/>
/// translation. There is no automatic retry — retrying a non-idempotent authorization risks
/// double-charging (ADR-0001). Every call that reaches the bank (success or failure) is timed and
/// recorded to <c>payments.bank.call.duration</c> (ADR-0007); a genuine caller cancellation is not
/// recorded, since it isn't a bank latency/availability event.
/// </summary>
public sealed class AcquiringBankClient : IAcquiringBankClient
{
    // Single acquirer in this build; ADR-0006's multi-acquirer routing is out of scope, so the
    // acquirer tag is a constant rather than a per-request value.
    private const string Acquirer = "simulator";

    private readonly HttpClient _httpClient;
    private readonly PaymentMetrics _metrics;

    public AcquiringBankClient(HttpClient httpClient, PaymentMetrics metrics)
    {
        _httpClient = httpClient;
        _metrics = metrics;
    }

    public async Task<BankPaymentResponse> ProcessPaymentAsync(BankPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        void Record(BankCallOutcome outcome) =>
            _metrics.RecordBankCall(stopwatch.Elapsed.TotalMilliseconds, Acquirer, outcome);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("payments", request, cancellationToken);

            // A 400 means the bank rejected the request as incomplete/malformed. Merchant input is
            // already validated, so this is a defect in how we built the bank request — an internal
            // error, not the bank being unavailable (a retry would never succeed).
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // A gateway-side defect, not a bank-availability failure — its own outcome bucket so
                // it never inflates the availability/failure-rate signal (ADR-0007, R-002).
                Record(BankCallOutcome.InvalidRequest);
                throw new InvalidBankRequestException(
                    "The acquiring bank rejected the request as invalid (400 Bad Request).");
            }

            if (!response.IsSuccessStatusCode)
            {
                Record(BankCallOutcome.Error);
                throw new BankUnavailableException(
                    $"The acquiring bank returned an unexpected status code {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<BankPaymentResponse>(cancellationToken);

            if (result is null)
            {
                Record(BankCallOutcome.Error);
                throw new BankUnavailableException("The acquiring bank returned an empty response body.");
            }

            Record(BankCallOutcome.Success);
            return result;
        }
        // A cancellation the caller asked for is a genuine cancellation and must propagate. Any
        // other cancellation is the client-side timeout firing (ADR-0001) — treat it as the bank
        // being unavailable.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Record(BankCallOutcome.Timeout);
            throw new BankUnavailableException("The acquiring bank call timed out.");
        }
        catch (HttpRequestException ex)
        {
            Record(BankCallOutcome.Error);
            throw new BankUnavailableException("The acquiring bank could not be reached.", ex);
        }
        // A 2xx whose body is malformed JSON (JsonException) or an unexpected content type
        // (NotSupportedException) means we can't read a definitive answer — still "we don't know".
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Record(BankCallOutcome.Error);
            throw new BankUnavailableException("The acquiring bank returned an unreadable response body.", ex);
        }
    }
}
