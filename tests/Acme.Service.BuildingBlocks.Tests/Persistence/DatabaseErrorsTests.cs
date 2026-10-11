using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

public sealed class DatabaseErrorsTests
{
    public static TheoryData<string, Exception, ErrorType, string> MappedExceptions() => new()
    {
        { "concurrency", new DbUpdateConcurrencyException("stale"), ErrorType.Conflict, "concurrency_conflict" },
        { "unique", new DbUpdateException("save", new PostgresException("dup", "ERROR", "ERROR", "23505")), ErrorType.Conflict, "unique_violation" },
        { "transient", new NpgsqlException("io", new IOException("reset")), ErrorType.Unavailable, "database_unavailable" },
        { "wrapped transient", new InvalidOperationException("likely transient", new NpgsqlException("io", new IOException("reset"))), ErrorType.Unavailable, "database_unavailable" },
        { "retries exhausted", new RetryLimitExceededException("exhausted"), ErrorType.Unavailable, "database_unavailable" },
        { "timeout", new TimeoutException(), ErrorType.Unavailable, "database_unavailable" },
    };

    [Theory]
    [MemberData(nameof(MappedExceptions))]
    public void TryMap_KnownFailure_ReturnsMappedError(string scenario, Exception exception, ErrorType type, string code)
    {
        DatabaseErrors.TryMap(exception, out var error).ShouldBeTrue(scenario);

        error.ShouldNotBeNull().Type.ShouldBe(type);
        error.Code.ShouldBe(code);
    }

    [Fact]
    public void TryMap_OtherPostgresError_IsNotMapped()
    {
        var exception = new DbUpdateException("save", new PostgresException("fk", "ERROR", "ERROR", "23503"));

        DatabaseErrors.TryMap(exception, out var error).ShouldBeFalse();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryMap_UnrelatedException_IsNotMapped() =>
        DatabaseErrors.TryMap(new ArgumentException("bad"), out _).ShouldBeFalse();
}
