using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.Testing.Contracts;

namespace Acme.Service.Infra.Tests.InMemory;

public sealed class InMemoryCacheContractTests : CacheContractTests
{
    protected override ValueTask<ICache> CreateCacheAsync() => ValueTask.FromResult<ICache>(new InMemoryCache());
}
