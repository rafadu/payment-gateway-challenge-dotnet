using FluentValidation;

using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Validation;

/// <summary>
/// Validates a <see cref="PostPaymentRequest"/> against the assessment's requirements table plus
/// the <c>Amount &gt; 0</c> extension (ADR-0002). The supported-currency allow-list is supplied by
/// configuration (design.md) rather than hardcoded, and "now" is taken from an injected
/// <see cref="TimeProvider"/> so the expiry-in-future rule stays deterministic under test.
/// </summary>
public class PostPaymentRequestValidator : AbstractValidator<PostPaymentRequest>
{
    public PostPaymentRequestValidator(IEnumerable<string> supportedCurrencies, TimeProvider timeProvider)
    {
        var currencies = new HashSet<string>(supportedCurrencies, StringComparer.OrdinalIgnoreCase);

        RuleFor(x => x.CardNumber)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Card number is required.")
            .Matches("^[0-9]+$").WithMessage("Card number must contain only digits.")
            .Length(14, 19).WithMessage("Card number must be between 14 and 19 digits long.");

        RuleFor(x => x.ExpiryMonth)
            .InclusiveBetween(1, 12).WithMessage("Expiry month must be between 1 and 12.");

        // The month+year combination must not be in the past. Reported against ExpiryYear so the
        // error attaches to a single, predictable property. Skipped when the month is itself out of
        // range (that is the ExpiryMonth rule's job) to avoid a confusing second error.
        RuleFor(x => x.ExpiryYear)
            .Must((request, _) => ExpiryIsInTheFuture(request, timeProvider))
            .When(x => x.ExpiryMonth is >= 1 and <= 12)
            .WithMessage("Card expiry must be in the future.");

        RuleFor(x => x.Currency)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-character ISO code.")
            .Must(currencies.Contains).WithMessage("Currency is not supported.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.");

        RuleFor(x => x.Cvv)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("CVV is required.")
            .Matches("^[0-9]+$").WithMessage("CVV must contain only digits.")
            .Length(3, 4).WithMessage("CVV must be 3 or 4 digits long.");
    }

    private static bool ExpiryIsInTheFuture(PostPaymentRequest request, TimeProvider timeProvider)
    {
        if (request.ExpiryYear is < 1 or > 9999)
        {
            return false;
        }

        // A card is valid through the end of its expiry month, so the expiry instant is the first
        // moment of the following month.
        var expiry = new DateTimeOffset(request.ExpiryYear, request.ExpiryMonth, 1, 0, 0, 0, TimeSpan.Zero)
            .AddMonths(1);

        return expiry > timeProvider.GetUtcNow();
    }
}
