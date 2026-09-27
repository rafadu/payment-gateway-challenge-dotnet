namespace PaymentGateway.Api.Exceptions;

/// <summary>
/// Thrown when the acquiring bank rejects the call with <c>400 Bad Request</c> — meaning the
/// gateway sent a request missing a required field. Merchant input is already validated before
/// this point, so a bank <c>400</c> signals a defect in how the gateway built the bank request,
/// not a bank availability problem. It must therefore <b>not</b> be treated as a
/// <see cref="BankUnavailableException"/> (which surfaces as <c>503</c> and invites a retry that
/// could never succeed); it is an internal gateway error.
/// </summary>
public sealed class InvalidBankRequestException : Exception
{
    public InvalidBankRequestException(string message) : base(message)
    {
    }

    public InvalidBankRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
