using Microsoft.AspNetCore.Mvc;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Models.Responses;

namespace PaymentGateway.Api.Controllers;

/// <summary>
/// Merchant login. Unauthenticated by design — this is the step that mints the bearer token the
/// rest of the API requires (ADR-0010).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ITokenIssuanceService _tokenIssuance;

    public AuthController(ITokenIssuanceService tokenIssuance)
    {
        _tokenIssuance = tokenIssuance;
    }

    [HttpPost("token")]
    public async Task<IActionResult> Token([FromBody] TokenRequest request, CancellationToken cancellationToken)
    {
        // Required fields. Guarding here also keeps a null/empty clientId from reaching the cache.
        if (request is null || string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "clientId and clientSecret are required."
            });
        }

        var issued = await _tokenIssuance.IssueTokenAsync(request.ClientId, request.ClientSecret, cancellationToken);

        // Uniform 401 for both unknown client and wrong secret (ADR-0010) — no existence leak.
        if (issued is null)
        {
            return Unauthorized();
        }

        return Ok(new TokenResponse
        {
            AccessToken = issued.AccessToken,
            ExpiresIn = issued.ExpiresInSeconds
        });
    }
}
