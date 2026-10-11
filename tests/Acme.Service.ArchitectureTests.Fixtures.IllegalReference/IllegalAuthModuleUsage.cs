using Acme.Service.Modules.Auth;

namespace Acme.Service.Modules.Orders.Illegal;

/// <summary>
/// Deliberate R1 violation: a type in another module's namespace depends on the Auth module's non-Contracts
/// assembly. Never copy this pattern; modules talk to each other only through <c>*.Contracts</c> (INV-01).
/// </summary>
public static class IllegalAuthModuleUsage
{
    /// <summary>Creates the Auth module directly, which only the Host may do.</summary>
    public static AuthModule CreateAuthModule() => new();
}
