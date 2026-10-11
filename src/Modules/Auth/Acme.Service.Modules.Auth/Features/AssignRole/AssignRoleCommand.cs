using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Persistence.Configurations;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.AssignRole;

/// <summary>Grants role <paramref name="Role"/> to user <paramref name="UserId"/>; idempotent.</summary>
/// <param name="ActorId">The administrator making the change (<c>sub</c>), for the security log.</param>
/// <param name="UserId">The user.</param>
/// <param name="Role">The role name (one of the code-owned roles, §0.8).</param>
internal sealed record AssignRoleCommand(Guid ActorId, Guid UserId, string Role) : ICommand<Done>;

internal sealed class AssignRoleValidator : AbstractValidator<AssignRoleCommand>
{
    public AssignRoleValidator()
    {
        RuleFor(c => c.Role).NotEmpty().MaximumLength(IdentityModelLimits.Default);
    }
}
