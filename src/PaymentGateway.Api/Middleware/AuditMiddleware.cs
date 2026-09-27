using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Middleware that persists an <see cref="AuditRecord"/> for every processed request
/// (ADR-0004, audit half). Runs as middleware (not a resource filter) so it captures
/// <c>401</c>s as well as <c>201</c>/<c>400</c>/<c>503</c> — the resource-filter approach can't see
/// requests rejected by <c>app.UseAuthorization()</c> before MVC starts. Captures timestamp, JWT
/// <c>sub</c>, method, path, response status code, outcome label
/// (Authorized/Declined/ValidationRejected/etc.), total duration, and a masked request summary
/// for POST /api/payments — the full PAN and the CVV are never persisted.
/// </summary>
public sealed class AuditMiddleware
{
    /// <summary>HttpContext.Items key controllers use to override the auto-derived outcome label.</summary>
    public const string OutcomeItemKey = "Audit.Outcome";

    private readonly RequestDelegate _next;
    private readonly IAuditStore _store;

    public AuditMiddleware(RequestDelegate next, IAuditStore store)
    {
        _next = next;
        _store = store;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Allow the body to be read both here AND by the model binder later in the pipeline.
        context.Request.EnableBuffering();
        var stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        var merchantId = context.User.FindFirstValue("sub") ?? string.Empty;
        var statusCode = context.Response.StatusCode;
        var outcome = ResolveOutcome(context, statusCode);

        var record = new AuditRecord
        {
            Timestamp = DateTime.UtcNow,
            MerchantId = merchantId,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            StatusCode = statusCode,
            Outcome = outcome,
            DurationMs = stopwatch.ElapsedMilliseconds,
            RequestSummary = await BuildMaskedSummaryAsync(context.Request)
        };

        // Best-effort: a failure to write the audit must not break the response that's already
        // been written by the inner pipeline.
        try
        {
            await _store.WriteAsync(record, context.RequestAborted);
        }
        catch
        {
            // Swallowed intentionally — audit is a forensic concern, not a transactional one.
        }
    }

    private static async Task<IReadOnlyDictionary<string, object?>?> BuildMaskedSummaryAsync(HttpRequest request)
    {
        // POST /api/payments is the only endpoint with a body worth masking. GET/DELETE/etc. return
        // null (a GET to /api/payments/{id} carries no PAN / CVV).
        if (!HttpMethods.IsPost(request.Method)) return null;
        if (!string.Equals(request.Path, "/api/payments", StringComparison.OrdinalIgnoreCase)) return null;

        request.Body.Position = 0;
        PostPaymentRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<PostPaymentRequest>(
                request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                request.HttpContext.RequestAborted);
        }
        catch
        {
            // Malformed body — model binding will reject this with 400, but we have nothing
            // meaningful to record. Skip the summary.
            return null;
        }
        finally
        {
            // Rewind so the model binder can read the body again.
            request.Body.Position = 0;
        }

        if (body is null) return null;

        return new Dictionary<string, object?>
        {
            { "cardNumberLastFour", ExtractLastFour(body.CardNumber) },
            { "expiryMonth", body.ExpiryMonth },
            { "expiryYear", body.ExpiryYear },
            { "currency", body.Currency },
            { "amount", body.Amount }
            // CVV intentionally omitted — never persisted (PCI-DSS, ADR-0004).
        };
    }

    private static string ExtractLastFour(string? cardNumber)
    {
        if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length < 4) return string.Empty;
        return cardNumber[^4..];
    }

    private static string ResolveOutcome(HttpContext httpContext, int statusCode)
    {
        // The producer (controller / service) can override the outcome via HttpContext.Items. This
        // is how the controller reports Authorized vs Declined — both are 201s, so the status
        // code alone can't distinguish them.
        if (httpContext.Items.TryGetValue(OutcomeItemKey, out var raw) && raw is string overridden)
        {
            return overridden;
        }

        return statusCode switch
        {
            201 => "Adjudicated",
            400 => "ValidationRejected",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "NotFound",
            409 => "Conflict",
            422 => "HashMismatch",
            500 => "InternalError",
            503 => "BankUnavailable",
            _ => "Unknown"
        };
    }
}