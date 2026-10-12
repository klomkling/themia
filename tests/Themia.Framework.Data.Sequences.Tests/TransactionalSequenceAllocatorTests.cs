using System.Data.Common;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class TransactionalSequenceAllocatorTests
{
    private static TransactionalSequenceAllocator CreateAllocator(
        string? tenantId = "acme",
        SequenceEngine engine = SequenceEngine.Postgres)
    {
        var options = new SequenceOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres",
            Engine = engine
        };
        var tenantContext = new TenantContext(tenantId is null ? null : new TenantId(tenantId));
        return new TransactionalSequenceAllocator(options, tenantContext);
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNullTransaction_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextInTransactionAsync(null!, "DocNo:TaxInvoice:2026-10"));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithNullTransaction_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextHostInTransactionAsync(null!, "DocNo:TaxInvoice:2026-10"));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), null!));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), string.Empty));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), null!));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), string.Empty));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithOverLengthKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();
        var overLengthKey = new string('x', SequenceProvider.MaxSequenceKeyLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), overLengthKey));

        Assert.Contains("exceeding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithOverLengthKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();
        var overLengthKey = new string('x', SequenceProvider.MaxSequenceKeyLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), overLengthKey));

        Assert.Contains("exceeding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNoAmbientTenant_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator(tenantId: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no ambient tenant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextInTransactionAsync_WhenTransactionHasNoConnection_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator();
        var txWithoutConnection = new FakeDbTransaction(connection: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextInTransactionAsync(txWithoutConnection, "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no associated connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WhenTransactionHasNoConnection_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator();
        var txWithoutConnection = new FakeDbTransaction(connection: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextHostInTransactionAsync(txWithoutConnection, "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no associated connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeDbTransaction(DbConnection? connection = null) : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
