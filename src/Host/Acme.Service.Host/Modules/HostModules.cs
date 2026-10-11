using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth;
// <sample:orders>
using Acme.Service.Modules.Orders;
// </sample:orders>

namespace Acme.Service.Host.Modules;

/// <summary>
/// The modules hosted by this service, in registration order (auth first, then the others). The single
/// list used by the web app and the command hosts (<c>migrate</c>, <c>auth:grant-role</c>), so all see the same
/// module contexts.
/// </summary>
internal static class HostModules
{
    /// <summary>Creates the module instances.</summary>
    public static IModule[] Create() =>
    [
        new AuthModule(),
        // <sample:orders>
        new OrdersModule(),
        // </sample:orders>
    ];
}
