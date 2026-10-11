using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acme.Service.BuildingBlocks.Persistence.DesignTime;

/// <summary>
/// Base for a module's design-time factory used by <c>dotnet ef</c> (migrations add/list/script). Builds the
/// context with the same Npgsql options as the runtime registration. The connection string comes from the
/// <c>ConnectionStrings__PostgresMigrations</c> environment variable, else <see cref="DefaultConnectionString"/>
/// (enough for <c>migrations add</c>, which does not connect).
/// </summary>
/// <remarks>The derived factory may be <c>internal</c>: EF tools discover non-public factories (WH-09).</remarks>
/// <typeparam name="TContext">The module context.</typeparam>
public abstract class ModuleDesignTimeFactoryBase<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : ModuleDbContext
{
    /// <summary>Environment variable read for the connection string.</summary>
    public const string ConnectionStringEnvironmentVariable = "ConnectionStrings__" + ConnectionStringNames.Migrations;

    /// <summary>Local development default; contains no password.</summary>
    public const string DefaultConnectionString = "Host=localhost;Port=5432;Database=acme_service;Username=postgres";

    /// <summary>The module name, equal to the context schema.</summary>
    protected abstract string ModuleKey { get; }

    /// <inheritdoc />
    public TContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnectionString;
        }

        var options = new DbContextOptionsBuilder<TContext>();
        NpgsqlContextOptions.Configure(options, connectionString, ModuleKey, typeof(TContext).Assembly);
        return CreateContext(options.Options);
    }

    /// <summary>Creates the context with <paramref name="options"/>.</summary>
    protected abstract TContext CreateContext(DbContextOptions<TContext> options);
}
