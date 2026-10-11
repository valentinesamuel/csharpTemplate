using Acme.Service.Testing.Containers;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>One Postgres container shared by the Auth integration tests; each test creates its own database.</summary>
[CollectionDefinition(Name)]
public sealed class AuthDatabaseGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "auth-database";
}
