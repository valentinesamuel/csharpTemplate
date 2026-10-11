using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.DesignTime;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;

/// <summary>Test aggregate with a unique name and an xmin row version.</summary>
internal sealed class Widget
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public int Quantity { get; set; }

    public uint Version { get; private set; }
}

/// <summary>Test module context (schema <c>widgets</c>) with one migration.</summary>
internal sealed class WidgetsDbContext(DbContextOptions<WidgetsDbContext> options) : ModuleDbContext(options)
{
    public const string ModuleKey = "widgets";

    public override string Schema => ModuleKey;

    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Widget>(widget =>
        {
            widget.HasKey(w => w.Id);
            widget.Property(w => w.Name).HasMaxLength(100);
            widget.HasIndex(w => w.Name).IsUnique();
            widget.Property(w => w.Version).IsRowVersion();
        });
}

/// <summary>Design-time factory; internal on purpose to verify WH-09.</summary>
internal sealed class WidgetsDesignTimeFactory : ModuleDesignTimeFactoryBase<WidgetsDbContext>
{
    protected override string ModuleKey => WidgetsDbContext.ModuleKey;

    protected override WidgetsDbContext CreateContext(DbContextOptions<WidgetsDbContext> options) => new(options);
}
