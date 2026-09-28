using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Middleware that persists an <see cref="AuditRecord"/> for POST /api/payments (ADR-0004,
/// audit half). Runs as middleware (not a resource filter) so it captures <c>401</c>s as well as
/// <c>201</c>/<c>400</c>/<c>503</c> — the resource-filter approach can't see requests rejected
/// by <c>app.UseAuthorization()</c> before MVC starts. Captures timestamp, JWT <c>sub</c>,
/// method, path, response status code, outcome label (Authorized/Declined/ValidationRejected/etc.),
/// total duration, and a masked request summary (the full PAN and the CVV are never persisted).
/// </summary>
/// <remarks>
/// <para>Audit scope is intentionally narrow: only POST /api/payments. The original design
/// (negative-list: audit everything except infrastructure endpoints) created two problems:
/// (a) every successful GET /api/payments/{id} hit the audit store — a high-volume merchant
/// paying for forensic trail they don't need; (b) the post-mortem signal-to-noise on the trail
/// degraded as the read-path traffic grew. The positive-list approach matches the actual
/// security review need: the trail records payment attempts and their outcomes, not the
/// retrieval of records that were already approved at creation time.</para>
/// </remarks>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuditStore _store;

    public AuditMiddleware(RequestDelegate next, IAuditStore store)
    {
        _next = next;
        _store = store;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldAudit(context))
        {
            await _next(context);
            return;
        }

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

    // Positive-list audit: only POST /api/payments. GETs (retrieval of already-authorized
    // payments) and POST /api/auth/token (login, no payment event) are not audited.
    private static bool ShouldAudit(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.StartsWithSegments("/api/payments", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<string, object?>?> BuildMaskedSummaryAsync(HttpRequest request)
    {
        // POST /api/payments is the only endpoint in the audit scope, so this is always a POST
        // body — but the guard stays defensive in case the scope widens later.
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
        if (httpContext.Items.TryGetValue(AuditConventions.OutcomeItemKey, out var raw) && raw is string overridden)
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