using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.LogoutAll;

/// <summary>Ends every session of the caller.</summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
internal sealed record LogoutAllCommand(Guid UserId) : ICommand<Done>;
