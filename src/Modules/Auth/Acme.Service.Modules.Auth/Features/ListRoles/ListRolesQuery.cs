using Acme.Service.BuildingBlocks.UseCases;

namespace Acme.Service.Modules.Auth.Features.ListRoles;

/// <summary>Every role with the permissions it grants.</summary>
internal sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleResponse>>;

/// <summary>A role and the permissions it grants, ordered by name.</summary>
/// <param name="Name">The role name.</param>
/// <param name="Permissions">The permission names.</param>
internal sealed record RoleResponse(string Name, IReadOnlyList<string> Permissions);
