using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.InMemory;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.InMemory;

/// <summary>Path-traversal defences of <see cref="LocalDiskBlobStore"/> (plan P7 failure mode).</summary>
public sealed class LocalDiskBlobStoreSecurityTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "service-tests", "blob-security", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;
    private readonly LocalDiskBlobStore _store;

    public LocalDiskBlobStoreSecurityTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(_outside);
        _store = new LocalDiskBlobStore(Options.Create(new LocalBlobOptions { RootPath = _root }));
    }

    public static TheoryData<string> TraversalKeys => new()
    {
        "../../etc/passwd",
        "../outside/secret.txt",
        "a/../../outside/secret.txt",
        "/etc/passwd",
        "C:\\Windows\\win.ini",
        "C:/Windows/win.ini",
        "\\\\server\\share\\file",
        "..\\..\\etc\\passwd",
        "a\\..\\..\\b",
        "%2e%2e%2f%2e%2e%2fetc%2fpasswd",
        "..%2f..%2fetc%2fpasswd",
        "%252e%252e%252f",
        "a/%2e%2e/b",
        "a\0b",
        "a/b\0/../../c",
        "a/./b",
        "./a",
        "a//b",
        "a/",
        "..",
        ".",
        "",
        " ",
        "a/b c",
        "~/file",
        "a:b",
    };

    [Theory]
    [MemberData(nameof(TraversalKeys))]
    public void ResolvePath_TraversalOrInvalidKey_Throws(string key) =>
        Should.Throw<ArgumentException>(() => _store.ResolvePath(key));

    [Theory]
    [MemberData(nameof(TraversalKeys))]
    public void BlobKeyCreate_TraversalOrInvalidKey_Throws(string key) =>
        Should.Throw<ArgumentException>(() => BlobKey.Create(key));

    [Fact]
    public void ResolvePath_ValidNestedKey_StaysUnderRoot()
    {
        var path = _store.ResolvePath("orders/0190/invoice.pdf");

        path.ShouldStartWith(_store.RootPath + Path.DirectorySeparatorChar);
        path.ShouldBe(Path.Combine(_store.RootPath, "orders", "0190", "invoice.pdf"));
    }

    [Fact]
    public async Task PutAsync_ValidKey_WritesOnlyUnderRoot()
    {
        using var content = new MemoryStream([1, 2, 3]);

        await _store.PutAsync(BlobKey.Create("a/b/c.bin"), content, "application/octet-stream", Ct);

        File.ReadAllBytes(Path.Combine(_store.RootPath, "a", "b", "c.bin")).ShouldBe(new byte[] { 1, 2, 3 });
        Directory.EnumerateFileSystemEntries(_outside).ShouldBeEmpty();
        Directory.EnumerateFiles(_store.RootPath, "*.tmp~", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task PutAsync_DirectorySymlinkEscapingRoot_IsRejectedAndWritesNothingOutside()
    {
        Directory.CreateSymbolicLink(Path.Combine(_store.RootPath, "link"), _outside);
        using var content = new MemoryStream([1]);

        await Should.ThrowAsync<ArgumentException>(
            () => _store.PutAsync(BlobKey.Create("link/escaped.txt"), content, "text/plain", Ct));

        Directory.EnumerateFileSystemEntries(_outside).ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenReadAsync_FileSymlinkToOutside_IsRejected()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "secret", Ct);
        File.CreateSymbolicLink(Path.Combine(_store.RootPath, "secret.txt"), secret);

        await Should.ThrowAsync<ArgumentException>(() => _store.OpenReadAsync(BlobKey.Create("secret.txt"), Ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.ExistsAsync(BlobKey.Create("secret.txt"), Ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.DeleteAsync(BlobKey.Create("secret.txt"), Ct));
        File.Exists(secret).ShouldBeTrue();
    }

    [Fact]
    public async Task PutAsync_DanglingSymlinkToOutside_IsRejected()
    {
        var target = Path.Combine(_outside, "created-by-attacker.txt");
        File.CreateSymbolicLink(Path.Combine(_store.RootPath, "dangling.txt"), target);
        using var content = new MemoryStream([1]);

        await Should.ThrowAsync<ArgumentException>(
            () => _store.PutAsync(BlobKey.Create("dangling.txt"), content, "text/plain", Ct));

        File.Exists(target).ShouldBeFalse();
    }

    [Theory]
    [InlineData("relative/root")]
    [InlineData("")]
    public void Constructor_NonAbsoluteRoot_Throws(string root) =>
        Should.Throw<ArgumentException>(() => new LocalDiskBlobStore(Options.Create(new LocalBlobOptions { RootPath = root })));

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
