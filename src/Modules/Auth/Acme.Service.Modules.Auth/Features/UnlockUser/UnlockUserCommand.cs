using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.UnlockUser;

/// <summary>Unlocks user <paramref name="UserId"/> and resets the failed sign-in counter.</summary>
/// <param name="ActorId">The administrator making the change (<c>sub</c>), for the security log.</param>
/// <param name="UserId">The user.</param>
internal sealed record UnlockUserCommand(Guid ActorId, Guid UserId) : ICommand<Done>;
