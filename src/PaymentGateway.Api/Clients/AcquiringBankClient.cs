using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Exceptions;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Clients;

/// <summary>
/// Typed <see cref="HttpClient"/> adapter over the acquiring bank simulator. The base address and
/// the bounded timeout (ADR-0001) are configured on the injected <see cref="HttpClient"/> at
/// registration, so this class owns only the wire call and the failure-to-<see cref="BankUnavailableException"/>
/// translation. There is no automatic retry — retrying a non-idempotent authorization risks
/// double-charging (ADR-0001).
/// </summary>
public sealed class AcquiringBankClient : IAcquiringBankClient
{
    private readonly HttpClient _httpClient;

    public AcquiringBankClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<BankPaymentResponse> ProcessPaymentAsync(BankPaymentRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("payments", request, cancellationToken);

            // A 400 means the bank rejected the request as incomplete/malformed. Merchant input is
            // already validated, so this is a defect in how we built the bank request — an internal
            // error, not the bank being unavailable (a retry would never succeed).
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new InvalidBankRequestException(
                    "The acquiring bank rejected the request as invalid (400 Bad Request).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new BankUnavailableException(
                    $"The acquiring bank returned an unexpected status code {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<BankPaymentResponse>(cancellationToken);

            return result
                ?? throw new BankUnavailableException("The acquiring bank returned an empty response body.");
        }
        // A cancellation the caller asked for is a genuine cancellation and must propagate. Any
        // other cancellation is the client-side timeout firing (ADR-0001) — treat it as the bank
        // being unavailable.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BankUnavailableException("The acquiring bank call timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new BankUnavailableException("The acquiring bank could not be reached.", ex);
        }
        // A 2xx whose body is malformed JSON (JsonException) or an unexpected content type
        // (NotSupportedException) means we can't read a definitive answer — still "we don't know".
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new BankUnavailableException("The acquiring bank returned an unreadable response body.", ex);
        }
    }
}
