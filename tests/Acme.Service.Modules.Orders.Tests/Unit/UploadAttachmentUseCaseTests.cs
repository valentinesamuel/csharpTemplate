using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Acme.Service.Modules.Orders.Features.UploadAttachment;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Unit;

/// <summary>The blob-first ordering and its compensation (P17 failure modes).</summary>
public sealed class UploadAttachmentUseCaseTests
{
    private readonly FakeOrders _orders = new();
    private readonly FakeAttachments _attachments = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IBlobStore _blobs = Substitute.For<IBlobStore>();
    private readonly Guid _customer = Guid.CreateVersion7();
    private readonly Order _order;

    public UploadAttachmentUseCaseTests()
    {
        _order = Order.Place(_customer, "EUR", [new OrderLineDraft("SKU", 1, 1m)], DateTimeOffset.UnixEpoch);
        _orders.Stored = _order;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Execute_UnitOfWorkSucceeds_WritesTheBlobBeforeTheRow()
    {
        var calls = new List<string>();
        _blobs.When(b => b.PutAsync(Arg.Any<BlobKey>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(_ => calls.Add("blob"));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), false, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                calls.Add("transaction");
                return await call.Arg<Func<CancellationToken, Task<Result>>>()(Ct);
            });

        var result = await CreateUseCase().ExecuteAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(["blob", "transaction"]);
        _attachments.Added.ShouldHaveSingleItem().ContentType.ShouldBe("application/pdf");
        await _blobs.Received(1).PutAsync(
            Arg.Is<BlobKey>(k => k.Value == $"orders/{_order.Id}/{result.Value.Id}"), Arg.Any<Stream>(), "application/pdf", Arg.Any<CancellationToken>());
        await _blobs.DidNotReceiveWithAnyArgs().DeleteAsync(default!, Ct);
    }

    [Fact]
    public async Task Execute_UnitOfWorkFails_DeletesTheBlobAndReturnsTheError()
    {
        var error = Error.Unavailable("database_unavailable", "down");
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), false, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(error));

        var result = await CreateUseCase().ExecuteAsync(Command(), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
        await _blobs.Received(1).DeleteAsync(Arg.Is<BlobKey>(k => k.Value.StartsWith($"orders/{_order.Id}/", StringComparison.Ordinal)), CancellationToken.None);
    }

    [Fact]
    public async Task Execute_UnitOfWorkThrows_DeletesTheBlobAndRethrows()
    {
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), false, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _blobs.DeleteAsync(Arg.Any<BlobKey>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("compensation fails too"));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateUseCase().ExecuteAsync(Command(), Ct));

        await _blobs.Received(1).DeleteAsync(Arg.Any<BlobKey>(), CancellationToken.None);
    }

    [Fact]
    public async Task Execute_ContentNotAllowed_WritesNothing()
    {
        var result = await CreateUseCase().ExecuteAsync(Command([0x4D, 0x5A, 0x90, 0x00]), Ct);

        result.Error.Code.ShouldBe("unsupported_attachment_type");
        await _blobs.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default!, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Execute_OtherCustomersOrder_IsNotFoundAndWritesNothing()
    {
        var command = Command() with { Caller = new OrdersCaller(Guid.CreateVersion7(), CanManage: false) };

        var result = await CreateUseCase().ExecuteAsync(command, Ct);

        result.Error.ShouldBe(OrderErrors.NotFound);
        await _blobs.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default!, Ct);
    }

    private UploadAttachmentUseCase CreateUseCase() =>
        new(_orders, _attachments, _unitOfWork, _blobs, TimeProvider.System, NullLogger<UploadAttachmentUseCase>.Instance);

    private UploadAttachmentCommand Command(byte[]? content = null)
    {
        var bytes = content ?? [.. "%PDF-1.7 test"u8];
        return new UploadAttachmentCommand(_order.Id, new OrdersCaller(_customer, CanManage: false), "a.pdf", bytes.Length, new MemoryStream(bytes));
    }

    private sealed class FakeOrders : IOrderRepository
    {
        public Order? Stored { get; set; }

        public Task<Order?> GetForReadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Id == id ? Stored : null);

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => GetForReadAsync(id, cancellationToken);

        public Task<IReadOnlyList<Order>> ListForReadAsync(Specification<Order> specification, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(Order entity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Remove(Order entity) => throw new NotSupportedException();

        public Task<IReadOnlyList<Order>> ListAsync(Specification<Order> specification, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(Specification<Order> specification, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAttachments : IRepository<OrderAttachment, Guid>
    {
        public List<OrderAttachment> Added { get; } = [];

        public Task AddAsync(OrderAttachment entity, CancellationToken cancellationToken)
        {
            Added.Add(entity);
            return Task.CompletedTask;
        }

        public Task<OrderAttachment?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Added.SingleOrDefault(a => a.Id == id));

        public void Remove(OrderAttachment entity) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrderAttachment>> ListAsync(Specification<OrderAttachment> specification, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(Specification<OrderAttachment> specification, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
