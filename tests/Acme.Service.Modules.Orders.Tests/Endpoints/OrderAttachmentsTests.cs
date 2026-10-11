using System.Net;
using System.Net.Http.Headers;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Endpoints;

/// <summary>
/// P17 deliverable 5 on the Local blob provider: multipart upload with the magic-byte allowlist and the size limit, the
/// metadata row, and download (streamed, or 302 when the provider issues download URLs).
/// </summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrderAttachmentsTests(PostgresFixture postgres)
{
    private const long DefaultMaxBytes = 10L * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Upload_ValidPdf_Is201AndDownloadReturnsTheSameBytes()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();
        var pdf = Pdf(50_000);

        using var uploaded = await UploadAsync(host, customer, orderId, pdf, "invoice.pdf", "application/octet-stream");

        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(Ct));
        var attachment = await OrdersTestHost.JsonAsync(uploaded);
        var attachmentId = attachment.GetProperty("id").GetGuid();
        uploaded.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{orderId}/attachments/{attachmentId}");
        attachment.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        attachment.GetProperty("fileName").GetString().ShouldBe("invoice.pdf");
        attachment.GetProperty("contentType").GetString().ShouldBe("application/pdf");
        attachment.GetProperty("sizeBytes").GetInt64().ShouldBe(pdf.Length);
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_attachments WHERE order_id = @id", ("id", orderId))).ShouldBe(1);
        (await host.Factory.Services.GetRequiredService<IBlobStore>()
            .ExistsAsync(BlobKey.Create($"orders/{orderId}/{attachmentId}"), Ct)).ShouldBeTrue();

        using var downloaded = await host.SendAsync(HttpMethod.Get, uploaded.Headers.Location.ToString(), customer);

        downloaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        downloaded.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await downloaded.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(pdf);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 })]
    public async Task Upload_PngOrJpeg_IsAcceptedWithTheDetectedType(string expectedType, byte[] content)
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();

        using var uploaded = await UploadAsync(host, customer, orderId, content, "picture.bin", "text/plain");

        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(Ct));
        (await OrdersTestHost.JsonAsync(uploaded)).GetProperty("contentType").GetString().ShouldBe(expectedType);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Upload_ExecutableRenamedPdf_Is400AndStoresNothing()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();
        byte[] exe = [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF];

        using var rejected = await UploadAsync(host, customer, orderId, exe, "invoice.pdf", "application/pdf");

        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OrdersTestHost.ProblemCodeAsync(rejected)).ShouldBe("unsupported_attachment_type");
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_attachments")).ShouldBe(0);
        BlobFiles(host).ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(DefaultMaxBytes + 1)]
    [InlineData(DefaultMaxBytes + (512 * 1024))]
    public async Task Upload_Oversize_Is413AndStoresNothing(long size)
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();

        using var rejected = await UploadAsync(host, customer, orderId, Pdf(size), "big.pdf", "application/pdf");

        rejected.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await OrdersTestHost.ProblemCodeAsync(rejected)).ShouldBe("attachment_too_large");
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_attachments")).ShouldBe(0);
        BlobFiles(host).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Upload_WithoutFilePart_Is400()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();
        using var form = new MultipartFormDataContent { { new StringContent("hello"), "note" } };

        using var rejected = await host.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/attachments", customer, form);

        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OrdersTestHost.ProblemCodeAsync(rejected)).ShouldBe("invalid_attachment");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Attachments_OtherCustomer_Is404AndManagerMayUploadAndRead()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var owner = await host.UserAsync("owner@example.com", "Customer");
        var other = await host.UserAsync("other@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var orderId = (await host.CreateOrderAsync(owner, ("SKU", 1, 1m))).GetProperty("id").GetGuid();

        using (var foreign = await UploadAsync(host, other, orderId, Pdf(100), "x.pdf", "application/pdf"))
        {
            foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await OrdersTestHost.ProblemCodeAsync(foreign)).ShouldBe("order_not_found");
        }

        using var managed = await UploadAsync(host, staff, orderId, Pdf(100), "x.pdf", "application/pdf");
        managed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var path = managed.Headers.Location!.ToString();

        using (var foreignRead = await host.SendAsync(HttpMethod.Get, path, other))
        {
            foreignRead.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var ownerRead = await host.SendAsync(HttpMethod.Get, path, owner))
        {
            ownerRead.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var missing = await host.SendAsync(HttpMethod.Get, $"/api/v1/orders/{orderId}/attachments/{Guid.CreateVersion7()}", owner);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OrdersTestHost.ProblemCodeAsync(missing)).ShouldBe("attachment_not_found");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Download_ProviderWithDownloadUrls_Is302ToTheUrl()
    {
        var url = new Uri("https://blobs.example.test/signed?sig=abc");
        await using var host = await OrdersTestHost.CreateAsync(postgres, configureServices: services =>
        {
            var local = services.Last(d => d.ServiceType == typeof(IBlobStore) && !d.IsKeyedService);
            services.Remove(local);
            services.AddSingleton<IBlobStore>(sp => new UrlIssuingBlobStore(
                (IBlobStore)(local.ImplementationInstance
                    ?? local.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, local.ImplementationType!)),
                url));
        });
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var orderId = (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();
        using var uploaded = await UploadAsync(host, customer, orderId, Pdf(100), "x.pdf", "application/pdf");
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var redirected = await host.SendAsync(HttpMethod.Get, uploaded.Headers.Location!.ToString(), customer);

        redirected.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        redirected.Headers.Location.ShouldBe(url);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        OrdersTestHost host, TestUser user, Guid orderId, byte[] content, string fileName, string declaredType)
    {
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
        using var form = new MultipartFormDataContent { { file, "file", fileName } };
        return await host.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/attachments", user, form);
    }

    private static byte[] Pdf(long size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    private static string[] BlobFiles(OrdersTestHost host) =>
        Directory.Exists(host.Factory.BlobRootPath)
            ? Directory.GetFiles(host.Factory.BlobRootPath, "*", SearchOption.AllDirectories)
            : [];

    /// <summary>A blob store that issues download URLs (like S3 or Azure) on top of the Local provider.</summary>
    private sealed class UrlIssuingBlobStore(IBlobStore inner, Uri url) : IBlobStore
    {
        public Task PutAsync(BlobKey key, Stream content, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, contentType, cancellationToken);

        public Task<Stream?> OpenReadAsync(BlobKey key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);

        public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);

        public Task<Uri?> GetDownloadUrlAsync(BlobKey key, TimeSpan expiresIn, CancellationToken cancellationToken) =>
            Task.FromResult<Uri?>(url);
    }
}
