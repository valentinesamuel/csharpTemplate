namespace Acme.Service.Modules.Orders.Features.Shared;

/// <summary>The success value of a use case that returns nothing (the endpoint answers 204).</summary>
internal readonly record struct Done
{
    public static readonly Done Value;
}
