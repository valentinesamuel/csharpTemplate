using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.Options;

namespace Acme.Service.Infra.Tests.InMemory;

public sealed class LocalDiskBlobStoreContractTests : BlobStoreContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "service-tests", "blob-contract", Guid.NewGuid().ToString("N"));

    protected override bool SupportsDownloadUrls => false;

    protected override ValueTask<IBlobStore> CreateStoreAsync() =>
        ValueTask.FromResult<IBlobStore>(new LocalDiskBlobStore(Options.Create(new LocalBlobOptions { RootPath = _root })));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
