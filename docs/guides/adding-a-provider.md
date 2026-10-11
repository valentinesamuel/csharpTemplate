# Adding a provider

> Applies to: Acme.Service template

Infrastructure is reached only through ports in core BuildingBlocks (`ICache`, `IBlobStore`, `INotificationSender`, `IJobQueue`, `IDistributedLock`). Each implementation is a provider in its own project under `src/BuildingBlocks/Infra/`, selected at startup by one configuration value, `<Category>:Provider` ([ADR-0008](../adr/0008-provider-model.md), [Infra README](../../src/BuildingBlocks/Infra/README.md)).

The promise (INV-24): a new provider is one new `Infra.*` project, one registration line in the Host and one configuration value, with no change to any module or to core BuildingBlocks. A new deployment target for an existing provider is configuration only.

## Before you write code: is it configuration only?

| You want | What to do |
| --- | --- |
| Cloudflare R2, MinIO, RustFS, Backblaze B2, Ceph or another S3-compatible store | Configuration only, on the `S3` provider (worked example below). |
| Azure Blob with a managed or workload identity | Configuration only: `Blob:AzureBlob:ServiceUri` instead of a connection string. |
| Valkey, a managed Redis, Redis with TLS | Configuration only, on the `Redis` provider (`Cache:Redis:ConnectionString`, for example with `ssl=true`). |
| Any SMTP relay (SES SMTP, SendGrid SMTP, Postfix) | Configuration only, on the `Smtp` provider. |
| A different technology (Memcached, an email HTTP API, Quartz.NET, a cloud queue) | A new provider project (worked example below). |

## Worked example 1: Cloudflare R2 is configuration only

R2 speaks the S3 API, and the `S3` provider (`src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.S3/`) already handles S3-compatible endpoints: it uses the service URL instead of an AWS region endpoint, signs with the configured region, and computes checksums only when required, which S3-compatible stores expect.

```bash
Blob__Provider=S3
Blob__S3__ServiceUrl=https://<account-id>.r2.cloudflarestorage.com
Blob__S3__Region=auto
Blob__S3__Bucket=<bucket>
Blob__S3__AccessKey=<R2 access key id>        # secret
Blob__S3__SecretKey=<R2 secret access key>    # secret
```

- Put the two keys in your secret store (in Kubernetes, `acme-service-secrets`; see [secrets](../operations/secrets.md)) and the rest in the ConfigMap.
- Leave `Blob__S3__ForcePathStyle` at `false` unless your store needs path-style URLs (RustFS and MinIO do).
- Create the bucket beforehand, or set `Blob__S3__CreateIfMissing=true`.
- Download URLs are presigned S3 URLs valid for at most 15 minutes; R2 supports them, so attachment downloads redirect instead of streaming.

Restart the app: no code, no build. The template's tests run the S3 provider against RustFS (`tests/Acme.Service.Infra.Tests/S3/S3BlobStoreContractTests.cs`); R2 itself is not exercised by any test, so verify uploads and downloads once against a real bucket.

## Worked example 2: a new cache provider

Suppose you need Memcached. The steps are the same for any category.

### 1. Create the Infra project

```bash
dotnet new classlib -o src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Memcached
dotnet sln Acme.Service.slnx add src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Memcached/Acme.Service.BuildingBlocks.Infra.Memcached.csproj --solution-folder /src/BuildingBlocks/Infra/
```

- Remove the generated `Class1.cs` and `PropertyGroup` (`Directory.Build.props` sets them).
- Reference only `src/BuildingBlocks/Acme.Service.BuildingBlocks/Acme.Service.BuildingBlocks.csproj`; an Infra project never references modules, Web or (except Hangfire's migration hook) Persistence.
- Add the client package version to `Directory.Packages.props` and a versionless `PackageReference` to the project (INV-20). Pick the latest stable version, check its licence (no commercial licences, INV-25) and run `dotnet restore Acme.Service.slnx --force-evaluate`.
- Add `<InternalsVisibleTo Include="Acme.Service.Infra.Tests" />` if the tests need internals.

### 2. Implement the port

```csharp
internal sealed class MemcachedCache(/* client, options, logger */) : ICache
{
    // GetAsync<T>, SetAsync<T>(key, value, ttl), RemoveAsync, TryAddAsync<T>(key, value, ttl), IsAvailable
}
```

Match the behaviour the contract suite and the rest of the system expect (see `src/BuildingBlocks/Infra/Acme.Service.BuildingBlocks.Infra.Redis/RedisCache.cs`):

- values are serialised with System.Text.Json;
- `TryAddAsync` is atomic (it backs the idempotency filter);
- when the backend is down, `GetAsync` returns a miss, `SetAsync` and `RemoveAsync` are skipped with a rate-limited warning, `TryAddAsync` throws so idempotency can apply `Idempotency:FailMode`, and `IsAvailable` is false;
- calls never block for long: bound them with short timeouts;
- provider-specific exceptions stay in the Infra project; never add them to the core port.

### 3. Options and the registration

```csharp
public sealed class MemcachedCacheOptions
{
    public const string SectionName = "Cache:Memcached";
    public string? Servers { get; set; }
    // + a Validator : IValidateOptions<MemcachedCacheOptions> whose messages name the full key
    //   (for example "Cache:Memcached:Servers is required when Cache:Provider is Memcached.") and never echo secrets.
}

public sealed class MemcachedCacheRegistration : IProviderRegistration
{
    public const string ProviderName = "Memcached";

    public ProviderCategory Category => ProviderCategory.Cache;

    public string Name => ProviderName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MemcachedCacheOptions>()
            .Bind(configuration.GetSection(MemcachedCacheOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MemcachedCacheOptions>, MemcachedCacheOptions.Validator>());
        services.AddSingleton<ICache, MemcachedCache>();

        // Optional readiness check: report Degraded (never Unhealthy) so a cache outage keeps pods in rotation (D-20).
        services.AddHealthChecks()
            .AddCheck<MemcachedHealthCheck>("memcached", failureStatus: HealthStatus.Degraded, tags: ["ready"]);
    }
}
```

- The registration and its options class are the project's public surface; everything else is `internal`.
- The registration needs a parameterless constructor, because the Host adds it with `p.Add<T>()`.
- `Register` runs only when the provider is selected. Never contact the backend during startup.

### 4. One line in the Host

Reference the project from `src/Host/Acme.Service.Host/Acme.Service.Host.csproj` and add one line to the `AddProviders` block in `src/Host/Acme.Service.Host/Program.cs`:

```csharp
builder.AddProviders(p =>
{
    p.Add<InMemoryCacheRegistration>();
    p.Add<RedisCacheRegistration>();
    p.Add<MemcachedCacheRegistration>();   // new
    // ...
});
```

### 5. One configuration value

```bash
Cache__Provider=Memcached
Cache__Memcached__Servers=<host>:11211
```

An unknown or misspelled value stops startup and lists the registered names. Document the new keys in [configuration](configuration.md) and add them to `scripts/ci/check-config-keys.sh`.

### 6. Run the contract test suite

Every port has an abstract contract suite in `tests/Acme.Service.Testing/Contracts/` (`CacheContractTests`, `BlobStoreContractTests`, `NotificationSenderContractTests`, `JobQueueContractTests`, `DistributedLockContractTests`). Subclass it in `tests/Acme.Service.Infra.Tests/` (add a project reference to the new Infra project):

```csharp
[Trait("Category", "Integration")]
public sealed class MemcachedCacheContractTests(MemcachedFixture fixture) : CacheContractTests, IClassFixture<MemcachedFixture>
{
    protected override ValueTask<ICache> CreateCacheAsync() => ValueTask.FromResult<ICache>(fixture.CreateCache());
}
```

Put the container fixture (Testcontainers, pinned image tag) next to the others in `tests/Acme.Service.Testing/Containers/`. The suite checks round trips, TTL expiry, removal and the atomicity of `TryAddAsync`. Add registration tests (missing or invalid options fail with the key named) as in `tests/Acme.Service.Infra.Tests/Redis/RedisCacheRegistrationTests.cs`, and, if the backend can fail at run time, a "backend down" test as in `tests/Acme.Service.Infra.Tests/Redis/RedisDownTests.cs`.

The architecture tests need no change: R3 keeps the client namespace out of modules and core, and R10 keeps the reference in the Host.

## Where the promise has exceptions

Most providers fit the one-project, one-line, one-value shape. These do not, and the docs say so rather than hide it:

- **Providers with a schema or other migrate-time step.** The `migrate` host does not run the provider registry, so such a provider needs an extra Host line in `src/Host/Acme.Service.Host/Commands/MigrateCommand.cs`. Hangfire is the example: besides `p.Add<HangfireRegistration>()` it needs `HangfireRegistration.AddSchemaMigration(...)` in `MigrateCommand` and `app.MapHangfireDashboardIfEnabled()` in `Program.cs`, three Host touch points in total.
- **Development-only providers.** The list of providers refused in `Production` (D-21) is hard-coded in `src/Host/Acme.Service.Host/Providers/ProviderRegistry.cs`; a new development-only provider must be added there.
- **Every provider** also needs its project reference in the Host project file; that is part of "one line" in spirit but is a second edit.
