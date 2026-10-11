using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.LockUser;

/// <summary>Locks user <paramref name="UserId"/> indefinitely and ends every session.</summary>
/// <param name="ActorId">The administrator making the change (<c>sub</c>), for the security log.</param>
/// <param name="UserId">The user.</param>
internal sealed record LockUserCommand(Guid ActorId, Guid UserId) : ICommand<Done>;
