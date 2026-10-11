using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Base class of every module's EF Core context. A context maps only to its own schema (INV-06): the
/// default schema is <see cref="Schema"/>, which must equal the module name. Identifiers are snake_case
/// (plan §0.5). The module's <c>outbox_messages</c> and <c>inbox_messages</c> tables are part of every model.
/// Register derived contexts with
/// <see cref="PersistenceExtensions.AddModuleDbContext{TContext}"/>.
/// </summary>
/// <remarks>
/// Optimistic concurrency: declare a <see cref="uint"/> property configured with <c>IsRowVersion()</c>; the
/// Npgsql provider maps it to the Postgres <c>xmin</c> system column, so no extra column is created.
/// </remarks>
public abstract class ModuleDbContext : DbContext
{
    /// <summary>Initializes the context with options built by the module registration or design-time factory.</summary>
    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>The module's schema. Lower-case, equal to the module name (for example <c>orders</c>).</summary>
    public abstract string Schema { get; }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Applied here (not only in the DI registration) so design-time factories get the same model.
        optionsBuilder.UseSnakeCaseNamingConvention();
        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModel(modelBuilder);

        // Every module schema carries its own outbox_messages and inbox_messages tables (P6, INV-08, INV-09).
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }

    /// <summary>Configures the module's entities (typically <c>ApplyConfigurationsFromAssembly</c>).</summary>
    protected abstract void ConfigureModel(ModelBuilder modelBuilder);
}
