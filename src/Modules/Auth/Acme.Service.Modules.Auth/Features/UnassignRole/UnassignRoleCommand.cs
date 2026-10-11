using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Persistence.Configurations;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.UnassignRole;

/// <summary>Removes role <paramref name="Role"/> from user <paramref name="UserId"/>; idempotent.</summary>
/// <param name="ActorId">The administrator making the change (<c>sub</c>), for the security log.</param>
/// <param name="UserId">The user.</param>
/// <param name="Role">The role name.</param>
internal sealed record UnassignRoleCommand(Guid ActorId, Guid UserId, string Role) : ICommand<Done>;

internal sealed class UnassignRoleValidator : AbstractValidator<UnassignRoleCommand>
{
    public UnassignRoleValidator()
    {
        RuleFor(c => c.Role).NotEmpty().MaximumLength(IdentityModelLimits.Default);
    }
}
