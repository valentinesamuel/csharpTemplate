using Acme.Service.Testing.Containers;

namespace Acme.Service.Modules.Orders.Tests.Fixtures;

/// <summary>One Postgres container shared by the Orders integration tests; each test creates its own database.</summary>
[CollectionDefinition(Name)]
public sealed class OrdersDatabaseGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "orders-database";
}
