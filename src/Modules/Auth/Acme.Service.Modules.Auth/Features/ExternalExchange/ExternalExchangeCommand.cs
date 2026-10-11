using Acme.Service.Modules.Auth.Sessions;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.ExternalExchange;

/// <summary>The body of <c>POST /api/v1/auth/external/exchange</c>.</summary>
/// <param name="Code">The one-time code from the callback redirect.</param>
internal sealed record ExternalExchangeRequest(string? Code)
{
    // Never print the code (INV-28).
    public override string ToString() => nameof(ExternalExchangeRequest);
}

/// <summary>
/// Exchanges a one-time external login code for tokens. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>, like
/// sign-in: the code is consumed by its own atomic update, which must stay consumed even when a later check fails, and
/// the session is created in its own Auth unit of work. Validation and logging still run in the pipeline.
/// </summary>
/// <param name="Code">The one-time code.</param>
/// <param name="Client">Client details recorded with the refresh token.</param>
internal sealed record ExternalExchangeCommand(string? Code, ClientInfo Client)
{
    // Never print the code (INV-28).
    public override string ToString() => nameof(ExternalExchangeCommand);
}

/// <summary>Presence and length only; the format is checked by the use case with a generic 401.</summary>
internal sealed class ExternalExchangeValidator : AbstractValidator<ExternalExchangeCommand>
{
    /// <summary>Longest accepted code (a code is 43 characters).</summary>
    public const int CodeMaxLength = 128;

    public ExternalExchangeValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(CodeMaxLength);
    }
}
