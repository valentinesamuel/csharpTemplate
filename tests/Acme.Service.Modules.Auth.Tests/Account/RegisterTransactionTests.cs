using System.Net;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Register;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Account;

/// <summary>
/// INV-08: the registration outbox rows are written in the same database transaction as the user, so they commit or
/// roll back together.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class RegisterTransactionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_FailureAfterUserAndOutboxWritten_RollsBackBoth()
    {
        var probe = new TransactionProbe();
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var factory = new AccountWebFactory(
            connectionString,
            configureServices: services =>
            {
                // Registered after the shared behaviours, so it runs innermost: inside the command's transaction.
                services.AddSingleton(probe);
                services.AddScoped<IUseCaseBehavior<RegisterCommand, Done>, FailAfterFlushBehavior>();
            });
        using var client = new AccountClient(factory);

        using var response = await client.RegisterAsync("rollback@example.com");

        // Inside the transaction both the user and its two outbox rows existed...
        probe.UsersInTransaction.ShouldBe(1);
        probe.OutboxRowsInTransaction.ShouldBe(2);

        // ...and the failure rolled all of them back.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.users")).ShouldBe(0);
        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.user_roles")).ShouldBe(0);
        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.outbox_messages")).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_Success_CommitsUserAndOutboxRowsTogether()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        await using var factory = new AccountWebFactory(connectionString);
        using var client = new AccountClient(factory);

        using var response = await client.RegisterAsync("commit@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await AuthTestServices.ScalarAsync<long>(connectionString, "SELECT count(*) FROM auth.users")).ShouldBe(1);
        (await AuthTestServices.RowsAsync(connectionString, "SELECT type FROM auth.outbox_messages ORDER BY type"))
            .ShouldBe(["auth.email-verification-requested.v1", "auth.user-registered.v1"]);
    }

    /// <summary>What <see cref="FailAfterFlushBehavior"/> saw inside the transaction.</summary>
    internal sealed class TransactionProbe
    {
        public long UsersInTransaction { get; set; } = -1;

        public long OutboxRowsInTransaction { get; set; } = -1;
    }

    /// <summary>
    /// Lets registration run, flushes the pending changes (the outbox rows) into the open transaction, counts the rows
    /// through the same connection, then fails the command so the unit of work rolls back.
    /// </summary>
    internal sealed class FailAfterFlushBehavior(AuthDbContext context, TransactionProbe probe) : IUseCaseBehavior<RegisterCommand, Done>
    {
        public async Task<Result<Done>> HandleAsync(RegisterCommand input, UseCaseNext<Done> continuation, CancellationToken cancellationToken)
        {
            var result = await continuation(cancellationToken);
            result.IsSuccess.ShouldBeTrue();
            context.Database.CurrentTransaction.ShouldNotBeNull();

            await context.SaveChangesAsync(cancellationToken);
            probe.UsersInTransaction = await context.Database
                .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM auth.users")
                .SingleAsync(cancellationToken);
            probe.OutboxRowsInTransaction = await context.Database
                .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM auth.outbox_messages")
                .SingleAsync(cancellationToken);

            return Error.Unavailable("injected_failure", "Injected after the user and the outbox rows were written.");
        }
    }
}
