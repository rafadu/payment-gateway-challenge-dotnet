using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PaymentGateway.Api.Services;

/// <summary>
/// <see cref="IAsyncResourceFilter"/> that implements the <c>Idempotency-Key</c> HTTP header
/// contract on <c>POST /api/payments</c> (ADR-0003). Opt-in: when the header is absent the filter
/// is a pass-through, so callers who don't supply a key see no behaviour change.
///
/// A resource filter wraps the entire MVC pipeline (auth, model binding, action filters, action
/// method, result executor), which is why it's used here instead of an action filter — body
/// capture at the action-filter level interacts badly with <c>CreatedAtActionResult</c>'s
/// status-code set inside the result executor in TestHost.
///
/// Status mapping (per ADR):
/// <list type="bullet">
///   <item><b>New key</b> → run the pipeline, capture the response, cache it (201/400 = terminal)
///   or release the claim (5xx = transient, let the merchant retry once it clears).</item>
///   <item><b>Same key + same hash, in progress</b> → 409 Conflict.</item>
///   <item><b>Same key + same hash, completed</b> → replay the cached response verbatim — the
///   bank must not be called again.</item>
///   <item><b>Same key + different hash</b> → 422 Unprocessable Entity.</item>
/// </list>
/// </summary>
public sealed class IdempotencyResourceFilter : IAsyncResourceFilter
{
    /// <summary>The HTTP header this filter looks for. Public so tests can build requests against it.</summary>
    public const string HeaderName = "Idempotency-Key";

    private readonly IIdempotencyStore _store;

    public IdempotencyResourceFilter(IIdempotencyStore store) => _store = store;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!IsPostToPayments(context) || !TryReadKey(context, out var key))
        {
            await next();
            return;
        }

        var requestHash = await ComputeRequestHashAsync(context);

        if (requestHash is null)
        {
            // No body to hash (empty body) — fall through; model binding will reject the request
            // before it reaches the action.
            await next();
            return;
        }

        var claim = _store.TryClaim(key, requestHash);

        switch (claim.Outcome)
        {
            case IdempotencyClaimOutcome.NewClaim:
                await ExecuteAndCaptureAsync(context, next, key);
                break;

            case IdempotencyClaimOutcome.InProgress:
                await WriteProblemAsync(context.HttpContext, StatusCodes.Status409Conflict,
                    "An identical request with this Idempotency-Key is already in progress.");
                context.Result = new EmptyResult();
                break;

            case IdempotencyClaimOutcome.Completed:
                await WriteCachedAsync(context.HttpContext, claim.CachedResponse!);
                context.Result = new EmptyResult();
                break;

            case IdempotencyClaimOutcome.HashMismatch:
                await WriteProblemAsync(context.HttpContext, StatusCodes.Status422UnprocessableEntity,
                    "The Idempotency-Key has already been used with a different request body.");
                context.Result = new EmptyResult();
                break;
        }
    }

    private static bool IsPostToPayments(ResourceExecutingContext context)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method)) return false;
        var routeData = context.RouteData.Values;
        return routeData.TryGetValue("controller", out var controller)
            && string.Equals(controller?.ToString(), "Payments", StringComparison.Ordinal);
    }

    private static bool TryReadKey(ResourceExecutingContext context, out string key)
    {
        if (context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var raw = values.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                key = raw;
                return true;
            }
        }

        key = string.Empty;
        return false;
    }

    /// <summary>
    /// Deterministic hash of the POST request body. Public so tests can pre-seed the store with
    /// the same hash a real request will compute (used to simulate an in-progress duplicate
    /// without flakily racing two requests).
    /// </summary>
    public static string ComputeRequestHash(string body)
    {
        // SHA-256 of the raw request bytes — stable across binding details, but matches only if
        // the merchant sends byte-identical retries (same JSON, same ordering, same whitespace).
        // SHA-256 gives a fixed-length digest suitable as a dictionary key.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    private static async Task<string?> ComputeRequestHashAsync(ResourceExecutingContext context)
    {
        // Enable buffering so the request body can be read here AND by the model binder later.
        var request = context.HttpContext.Request;
        request.EnableBuffering();
        request.Body.Position = 0;
        using var sr = new StreamReader(request.Body, leaveOpen: true);
        var body = await sr.ReadToEndAsync();
        request.Body.Position = 0;
        return body.Length == 0 ? null : ComputeRequestHash(body);
    }

    private async Task ExecuteAndCaptureAsync(ResourceExecutingContext context, ResourceExecutionDelegate next, string key)
    {
        var response = context.HttpContext.Response;
        var originalBody = response.Body;
        using var captured = new MemoryStream();
        response.Body = captured;

        var executed = await next();

        response.Body = originalBody;

        // An unhandled exception that escaped the pipeline: don't cache a partial response.
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            _store.Release(key);
            return;
        }

        var status = response.StatusCode;
        var contentType = response.ContentType ?? "application/json";
        var location = response.Headers.TryGetValue("Location", out var loc) ? loc.ToString() : null;

        captured.Position = 0;
        var bodyBytes = captured.ToArray();

        if (IsTerminal(status))
        {
            _store.Complete(key, new CachedResponse(status, contentType, location, bodyBytes));
        }
        else
        {
            _store.Release(key);
        }

        // Write the captured body to the original stream so the client actually sees the response.
        await originalBody.WriteAsync(bodyBytes);
    }

    private static async Task WriteCachedAsync(HttpContext httpContext, CachedResponse cached)
    {
        var response = httpContext.Response;
        response.StatusCode = cached.StatusCode;
        response.ContentType = cached.ContentType;
        if (cached.Location is not null)
        {
            response.Headers["Location"] = cached.Location;
        }
        response.ContentLength = cached.Body.Length;
        await response.Body.WriteAsync(cached.Body);
    }

    private static async Task WriteProblemAsync(HttpContext httpContext, int statusCode, string title)
    {
        var problem = new ProblemDetails { Status = statusCode, Title = title };
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        var json = System.Text.Json.JsonSerializer.Serialize(problem);
        await httpContext.Response.WriteAsync(json);
    }

    private static bool IsTerminal(int statusCode) =>
        // 2xx success + 400 validation rejection are terminal and get cached. 5xx (including the
        // 503 bank-unavailable path) are transient — the merchant must be allowed to retry.
        statusCode is >= 200 and < 300 || statusCode == StatusCodes.Status400BadRequest;
}