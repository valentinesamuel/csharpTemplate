namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>Connection string keys under <c>ConnectionStrings</c> (plan §0.7).</summary>
public static class ConnectionStringNames
{
    /// <summary>Used by the web app (DML only).</summary>
    public const string App = "Postgres";

    /// <summary>Used by the <c>migrate</c> command and design-time tooling (DDL).</summary>
    public const string Migrations = "PostgresMigrations";
}

/// <summary>Selects which connection string module contexts are created with in the current process.</summary>
/// <param name="Name">The key under <c>ConnectionStrings</c>.</param>
internal sealed record ConnectionStringSelection(string Name);
