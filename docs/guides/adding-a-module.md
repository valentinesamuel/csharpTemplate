# Adding a module

> Applies to: Acme.Service template

A module is a vertical slice of the business with its own PostgreSQL schema, its own use cases and endpoints, and a small public surface in a `*.Contracts` project. This guide walks through adding one, using a hypothetical `Invoices` module (schema `invoices`). Read [module boundaries](../architecture/module-boundaries.md) and [data ownership](../architecture/data-ownership.md) first.

<!-- <sample:orders> -->

The Orders sample (`src/Modules/Orders/`) is a complete reference for every step below; its [README](../../src/Modules/Orders/README.md) maps each pattern to a file.

<!-- </sample:orders> -->

## The rules you must keep

- Other modules see only your Contracts project; your module sees only other modules' Contracts (INV-01).
- The only public type in the module assembly is `InvoicesModule`; use cases, endpoints, entities and the DbContext are `internal` (INV-02).
- No reference to any `Infra.*` project or provider SDK; use the ports in `src/BuildingBlocks/Acme.Service.BuildingBlocks/` (INV-03).
- EF Core types only in the `...Persistence` namespace (INV-04); use cases depend on `IRepository<,>`, `IUnitOfWork` or your own repository port.
- One schema, named after the module; no cross-schema joins or foreign keys (INV-06).

The architecture tests in `tests/Acme.Service.ArchitectureTests/` enforce all of these and discover modules from the project files, so they cover a new module without changes.

## 1. Create the two projects

```bash
dotnet new classlib -o src/Modules/Invoices/Acme.Service.Modules.Invoices.Contracts
dotnet new classlib -o src/Modules/Invoices/Acme.Service.Modules.Invoices
dotnet sln Acme.Service.slnx add src/Modules/Invoices/Acme.Service.Modules.Invoices.Contracts/Acme.Service.Modules.Invoices.Contracts.csproj --solution-folder /src/Modules/Invoices/
dotnet sln Acme.Service.slnx add src/Modules/Invoices/Acme.Service.Modules.Invoices/Acme.Service.Modules.Invoices.csproj --solution-folder /src/Modules/Invoices/
```

Delete the generated `Class1.cs` and the `PropertyGroup` the template adds: `Directory.Build.props` already sets the target framework, nullable, implicit usings and warnings-as-errors, and turns on XML documentation for `*.Contracts` projects (so every public Contracts member needs a `///` comment).

Contracts project references `BuildingBlocks` only:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\BuildingBlocks\Acme.Service.BuildingBlocks\Acme.Service.BuildingBlocks.csproj" />
</ItemGroup>
```

Module project:

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\..\..\BuildingBlocks\Acme.Service.BuildingBlocks\Acme.Service.BuildingBlocks.csproj" />
  <ProjectReference Include="..\..\..\BuildingBlocks\Acme.Service.BuildingBlocks.Persistence\Acme.Service.BuildingBlocks.Persistence.csproj" />
  <ProjectReference Include="..\..\..\BuildingBlocks\Acme.Service.BuildingBlocks.Web\Acme.Service.BuildingBlocks.Web.csproj" />
  <ProjectReference Include="..\Acme.Service.Modules.Invoices.Contracts\Acme.Service.Modules.Invoices.Contracts.csproj" />
  <!-- Other modules only through their Contracts, for example: -->
  <ProjectReference Include="..\..\Auth\Acme.Service.Modules.Auth.Contracts\Acme.Service.Modules.Auth.Contracts.csproj" />
</ItemGroup>
<ItemGroup>
  <InternalsVisibleTo Include="Acme.Service.Modules.Invoices.Tests" />
</ItemGroup>
```

No `Version` attributes: package versions live only in `Directory.Packages.props` (INV-20). Every project has a committed `packages.lock.json`; after adding projects or packages run `dotnet restore Acme.Service.slnx --force-evaluate` and commit the lock files.

## 2. Contracts: permissions and events

```csharp
using Acme.Service.BuildingBlocks.Security;

namespace Acme.Service.Modules.Invoices.Contracts;

/// <summary>Permissions owned by the Invoices module.</summary>
public static class InvoicesPermissions
{
    /// <summary>Read invoices.</summary>
    public const string Read = "invoices.read";

    /// <summary>The permissions with their seed roles, returned by IModule.Permissions.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Read, "Read invoices.", ["Admin", "Staff"]),
    ];
}
```

Default roles must be seed roles (`Admin`, `Staff`, `Customer`). Integration events go in `Contracts/IntegrationEvents/` ([integration events](integration-events.md)); query interfaces for other modules (like `IUserDirectory` in Auth) go in the Contracts root. See [permissions](permissions.md).

## 3. The module class

```csharp
// usings omitted: Acme.Service.BuildingBlocks.Security, Acme.Service.BuildingBlocks.Web.Modules, Microsoft.AspNetCore.Routing,
// Microsoft.Extensions.Configuration, Microsoft.Extensions.DependencyInjection(.Extensions), Microsoft.Extensions.Hosting
namespace Acme.Service.Modules.Invoices;

public sealed class InvoicesModule : IModule
{
    public string Name => "invoices";   // the schema and the key of the keyed unit of work and outbox

    public IReadOnlyList<PermissionDefinition> Permissions => InvoicesPermissions.Definitions;

    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        // options, integration event types and handlers, jobs and their schedulers
    }

    public void AddPersistence(IServiceCollection services, IConfiguration configuration) =>
        services.AddInvoicesPersistence(configuration, Name);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Usually empty: every IEndpoint in the assembly is discovered and mapped.
    }
}
```

`Name` is lower-case letters, digits and underscores, starting with a letter. Keep it in an internal constant (for example `InvoicesModuleKey.Value`) so use cases can name it in `[FromKeyedServices(...)]` without depending on persistence types. `AddModules` registers the use cases and validators of the module assembly; `AddPersistence` runs in the web app and in the `migrate` and `auth:grant-role` hosts, so keep it free of HTTP or provider dependencies.

## 4. Persistence

Everything EF goes under `Persistence/` in the namespace `Acme.Service.Modules.Invoices.Persistence`.

```csharp
internal sealed class InvoicesDbContext(DbContextOptions<InvoicesDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "invoices";

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InvoicesDbContext).Assembly);
}

internal sealed class InvoicesDesignTimeFactory : ModuleDesignTimeFactoryBase<InvoicesDbContext>
{
    protected override string ModuleKey => "invoices";

    protected override InvoicesDbContext CreateContext(DbContextOptions<InvoicesDbContext> options) => new(options);
}

internal static class InvoicesPersistence
{
    public static IServiceCollection AddInvoicesPersistence(this IServiceCollection services, IConfiguration configuration, string moduleKey)
    {
        services.AddModuleDbContext<InvoicesDbContext>(configuration, moduleKey)
            .AddRepository<Invoice, Guid>();
        return services;
    }
}
```

- `ModuleDbContext` sets the default schema, the snake_case naming convention and the module's `outbox_messages` and `inbox_messages` tables; the registration and the design-time factory put the migrations history table (`__ef_migrations_history`) inside the schema.
- `AddModuleDbContext` registers the context on `ConnectionStrings:Postgres` (or `PostgresMigrations` inside `migrate`), the keyed `IUnitOfWork` and `IOutbox` for the module, the outbox dispatcher, the readiness check target and the migration target. Use `.AddRepository<TEntity,TId>()` for the generic `IRepository<,>`, or declare your own repository interface in the domain and implement it under `Persistence/` for queries that need more.
- Conventions: plural snake_case tables, `id uuid` keys created with `Guid.CreateVersion7()`, `timestamptz` columns in UTC named `*_at`, and `TimeProvider` instead of `DateTime.UtcNow`.

## 5. Migrations

```bash
dotnet ef migrations add Initial \
  --project src/Modules/Invoices/Acme.Service.Modules.Invoices \
  --startup-project src/Host/Acme.Service.Host \
  --context InvoicesDbContext \
  --output-dir Persistence/Migrations
```

`dotnet ef` generates **public** classes; change the migration, its designer file and the model snapshot to `internal` (R5 fails otherwise). Migration files are treated as generated code by the `.editorconfig` section `[src/Modules/**/Migrations/*.cs]`. Migrations must be backwards compatible with the running release (expand/contract, see [migrations](../operations/migrations.md)).

## 6. Register the module in the Host

1. Reference the module from `src/Host/Acme.Service.Host/Acme.Service.Host.csproj`.
2. Add `new InvoicesModule()` to `src/Host/Acme.Service.Host/Modules/HostModules.cs`, after `AuthModule`. This single list feeds the web app, `migrate` and `auth:grant-role`, and its order is the migration order.

## 7. Use cases and endpoints

Add each use case as a folder under `Features/` ([adding a use case](adding-a-use-case.md)). Endpoints are `IEndpoint` classes under `/api/v1/invoices`, discovered automatically. Every endpoint requires an authenticated user unless it calls `.AllowAnonymous()`; an anonymous endpoint must also be added, with a reason, to `tests/Acme.Service.IntegrationTests/Security/AnonymousEndpointAllowlist.cs`, or the allowlist test fails.

## 8. Migrate and run

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate --list
DOTNET_ENVIRONMENT=Development dotnet run --project src/Host/Acme.Service.Host -- migrate
```

`migrate --list` prints `invoices (InvoicesDbContext): 1 pending`. `migrate` creates the schema, applies the migration and re-syncs the permission catalog, so the seed roles receive the new permissions. Until it has run, `/health/ready` is 503.

## 9. Tests

1. Create `tests/Acme.Service.Modules.<Name>.Tests/` (here `Acme.Service.Modules.Invoices.Tests`) as an xUnit v3 project shaped like `tests/Acme.Service.Modules.Auth.Tests/Acme.Service.Modules.Auth.Tests.csproj` (`OutputType` `Exe`, the shared test packages, references to the Host, the module and `tests/Acme.Service.Testing/`), and add it to the solution under `/tests/`.
2. Unit-test the domain and validators; integration-test the endpoints with a `ServiceWebFactory` subclass and a `PostgresFixture` database, marked `[Trait("Category", "Integration")]` ([testing](testing.md)).
3. Run the architecture tests: `dotnet test Acme.Service.slnx -c Release --filter "FullyQualifiedName~ArchitectureTests"`. They check boundaries, visibility, EF confinement, event names and that the context maps only its own schema.

## Checklist

- [ ] Contracts project references only `BuildingBlocks`; module project references only BuildingBlocks, Web, Persistence and Contracts projects.
- [ ] `InvoicesModule` is the only public type; migrations made `internal`.
- [ ] Schema name equals `IModule.Name`.
- [ ] Permissions declared with seed-role defaults; endpoints protected.
- [ ] Module added to the Host project and `HostModules.Create()`.
- [ ] Lock files regenerated with `--force-evaluate` and committed.
- [ ] `migrate` applied; architecture, unit and integration tests green.
