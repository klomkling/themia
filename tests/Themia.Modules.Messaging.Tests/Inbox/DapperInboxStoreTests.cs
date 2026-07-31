using System.Data.Common;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Messaging.Inbox;
using Themia.Modules.Messaging.Inbox;

using Xunit;

namespace Themia.Modules.Messaging.Tests.Inbox;

// F1: TryAdmitAsync must refuse to run outside the caller's ambient transaction — without one, the
// admission row could autocommit independently of the caller's state change, and a crash between the two
// would make a genuine redelivery look like correct deduplication (the exact loss window the inbox
// exists to close). The guard is engine-agnostic: it fires on connectionContext.CurrentTransaction alone,
// before the store ever opens a connection or reaches the injected per-engine dialect, so one dialect-blind
// test (proven never to reach the dialect, via a dialect stub that throws if invoked) covers Postgres,
// MySQL, and SQL Server identically — there is no per-engine branch for this guard to differ on.
public class DapperInboxStoreTests
{
    private sealed class NoTransactionConnectionContext : IDapperConnectionContext
    {
        public DbTransaction? CurrentTransaction => null;

        public Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "TryAdmitAsync must not open a connection before checking for an ambient transaction.");

        public Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeTransactionAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnreachableDialect : IInboxAdmissionDialect
    {
        public Task<bool> TryAdmitAsync(
            DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
            string? tenantId, string type, CancellationToken ct)
            => throw new InvalidOperationException(
                "TryAdmitAsync must not reach the dialect when there is no ambient transaction.");
    }

    [Fact]
    public async Task TryAdmitAsync_ShouldThrow_WhenNoAmbientTransaction()
    {
        var store = new DapperInboxStore(
            new NoTransactionConnectionContext(),
            new UnreachableDialect(),
            new TenantContext(new TenantId("acme")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TryAdmitAsync("peer-a", Guid.CreateVersion7(), "test.message.v1", CancellationToken.None));

        Assert.Contains("transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
