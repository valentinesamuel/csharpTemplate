namespace Acme.Service.BuildingBlocks.Security;

/// <summary>A permission declared by a module, plus the seed roles that receive it (plan §0.8).</summary>
/// <param name="Name">Lower-case dotted <c>&lt;resource&gt;.&lt;action&gt;</c>, for example <c>orders.read</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="DefaultRoles">Seed roles granted this permission on every <c>migrate</c> run.</param>
public sealed record PermissionDefinition(string Name, string Description, IReadOnlyList<string> DefaultRoles);
