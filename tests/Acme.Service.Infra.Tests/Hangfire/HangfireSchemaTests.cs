using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;

namespace Acme.Service.Infra.Tests.Hangfire;

/// <summary>INV-16: only the migrate step creates the Hangfire schema; a running provider never does.</summary>
[Trait("Category", "Integration")]
public sealed class HangfireSchemaTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SchemaStep_FreshDatabase_InstallsHangfireSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await HangfireTestDatabase.InstallSchemaAsync(connectionString, "hangfire", Ct);

        (await HangfireTestDatabase.SchemaExistsAsync(connectionString, "hangfire", Ct)).ShouldBeTrue();
        (await HangfireTestDatabase.SchemaVersionAsync(connectionString, "hangfire", Ct)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SchemaStep_RunTwice_IsIdempotent()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await HangfireTestDatabase.InstallSchemaAsync(connectionString, "hangfire", Ct);
        var version = await HangfireTestDatabase.SchemaVersionAsync(connectionString, "hangfire", Ct);

        await HangfireTestDatabase.InstallSchemaAsync(connectionString, "hangfire", Ct);

        (await HangfireTestDatabase.SchemaVersionAsync(connectionString, "hangfire", Ct)).ShouldBe(version);
    }

    [Fact]
    public async Task SchemaStep_CustomSchema_InstallsOnlyThatSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        await HangfireTestDatabase.InstallSchemaAsync(connectionString, "jobs_custom", Ct);

        (await HangfireTestDatabase.SchemaExistsAsync(connectionString, "jobs_custom", Ct)).ShouldBeTrue();
        (await HangfireTestDatabase.SchemaExistsAsync(connectionString, "hangfire", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderHost_SchemaMissing_StartsButNeverCreatesSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Services.AddLogging();
        builder.Services.AddSingleton(new JobContractProbe());
        builder.Services.AddScoped<JobContractScopedDependency>();
        HangfireTestDatabase.AddProvider(builder, connectionString);
        using var host = builder.Build();

        await host.StartAsync(Ct);
        var queue = host.Services.GetRequiredService<IJobQueue>();
        var enqueue = await Should.ThrowAsync<Exception>(
            () => queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(new JobContractPayload("m", 1, []), Ct));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        FindPostgresError(enqueue).ShouldNotBeNull().SqlState.ShouldBe(PostgresErrorCodes.UndefinedTable);
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
        (await HangfireTestDatabase.SchemaExistsAsync(connectionString, "hangfire", Ct)).ShouldBeFalse();
        await host.StopAsync(CancellationToken.None);
        (await HangfireTestDatabase.SchemaExistsAsync(connectionString, "hangfire", Ct)).ShouldBeFalse();
    }

    private static PostgresException? FindPostgresError(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
