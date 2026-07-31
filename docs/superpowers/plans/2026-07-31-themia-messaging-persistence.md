# Themia.Messaging Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Themia.Messaging` a working transactional outbox and deduplicating inbox on PostgreSQL, MySQL and SQL Server, and retrofit a batched purge onto the Notifications outbox that has been growing unbounded since 0.6.x.

**Architecture:** The generic drain machinery already exists in `Themia.Messaging` from an earlier step (`IOutboxDialect<TRow>`, `OutboxDrainer<TRow>`, `IOutboxDispatcher<TRow>`, `BackoffPolicy`, `DrainSignal`). This plan adds the *storage* underneath it: a `messaging` schema owned by FluentMigrator, per-engine dialect implementations, a repository-backed outbox store that joins the caller's unit of work, a Dapper-only inbox admission that commits inside the caller's transaction, and a purge that runs from the existing drain loop rather than a new scheduler.

**Tech Stack:** .NET 10, Dapper, FluentMigrator, Npgsql / MySqlConnector / Microsoft.Data.SqlClient, xUnit, Testcontainers.

## Global Constraints

- Spec of record: `docs/superpowers/specs/2026-07-31-themia-messaging-persistence-design.md` (rev 2).
- **All new packages target `net10.0`.** Not `net8.0;net10.0` — see coord #0050 for why the net8 leg was reversed.
- `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` are inherited from `Directory.Build.props`. **Every public member needs an XML doc comment or the build fails.**
- Every package with a `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` pair reports undeclared public members as **RS0016 errors**. After adding public API, run `dotnet build <proj> --no-incremental`, read the RS0016 list, and append the exact signatures to `PublicAPI.Unshipped.txt`.
- Central package management: a new `PackageReference` **must** have a matching `PackageVersion` in `Directory.Packages.props` or restore fails with NU1010.
- `System.Text.Json` only. **Never** introduce `Newtonsoft.Json`.
- Log via `ILogger<T>` only. No `Console.WriteLine`.
- FluentMigrator owns all DDL. **No module runs `dotnet ef migrations add`.**
- Never edit a deployed migration. New schema changes get a new migration file.
- Commit messages: `<type>: <subject>`, imperative, no co-author or "generated with" trailers.
- Run from `Packages/themia/`.

---

## File Structure

**New package `src/neutral/Themia.Messaging` (additions to the existing project):**
- `Outbox/IOutboxPurgeDialect.cs` — generic purge contract, one implementation per outbox table.
- `Inbox/IInboxPurgeDialect.cs` — inbox purge contract; messaging only.
- `Outbox/OutboxDrainerOptions.cs` *(modify)* — purge settings.
- `Outbox/OutboxDrainer.cs` *(modify)* — run purge from the existing loop.

**New package `src/neutral/Themia.Messaging.PostgreSql`:**
- `PostgresMessagingDialect.cs` — claim/complete/fail over `messaging.outbox_messages`.
- `PostgresMessagingPurgeDialect.cs` — batched purge for outbox and inbox.
- `PostgresInboxAdmission.cs` — insert-if-not-exists on the caller's transaction.
- `ServiceCollectionExtensions.cs` — DI entry point.

**New packages `src/neutral/Themia.Messaging.MySql` and `.SqlServer`:** the same four files per engine.

**New package `src/modules/Themia.Modules.Messaging`:**
- `Entities/MessageOutboxEntry.cs` — the persisted outbox row.
- `Migrations/MessagingSchemaMigration.cs` — the `messaging` schema.
- `Mapping/MessagingDapperMappings.cs`, `EntityConfiguration/MessageOutboxEntryConfiguration.cs`.
- `Stores/MessageOutboxStore.cs` — repository-backed `IMessageOutboxStore`.
- `Inbox/InboxPurgeService.cs` — `BackgroundService` for inbox retention.
- `MessagingModuleOptions.cs`, `MessagingModule.cs`, `DependencyInjection/MessagingServiceCollectionExtensions.cs`.

**Modified `src/modules/Themia.Modules.Notifications`:**
- `Migrations/NotificationsPurgeIndexMigration.cs` *(new)* — `(status, sent_at)` index.
- `Migrations/NotificationsSchemaMigration.cs:52` *(modify)* — correct the misleading comment.
- `NotificationsModuleOptions.cs` *(modify)* — opt-in purge settings.
- `DependencyInjection/NotificationsServiceCollectionExtensions.cs` *(modify)* — map purge options.
- The three `Themia.Modules.Notifications.{PostgreSql,MySql,SqlServer}` packages gain a purge dialect each.

---

### Task 1: Purge contracts and drain-loop integration

**Files:**
- Create: `src/neutral/Themia.Messaging/Outbox/IOutboxPurgeDialect.cs`
- Create: `src/neutral/Themia.Messaging/Inbox/IInboxPurgeDialect.cs`
- Modify: `src/neutral/Themia.Messaging/Outbox/OutboxDrainerOptions.cs`
- Modify: `src/neutral/Themia.Messaging/Outbox/OutboxDrainer.cs`
- Modify: `src/neutral/Themia.Messaging/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Messaging.Tests/Outbox/OutboxDrainerPurgeTests.cs`

**Interfaces:**
- Consumes: `IClaimedRow`, `IOutboxDialect<TRow>`, `IOutboxDispatcher<TRow>`, `DrainSignal`, `OutboxDrainerOptions<TRow>` (all existing).
- Produces: `IOutboxPurgeDialect<TRow>` with `PurgeSentAsync` / `PurgeDeadAsync`; `IInboxPurgeDialect` with `PurgeAdmittedAsync`; `OutboxDrainerOptions<TRow>` gains `PurgeEnabled`, `SentRetentionDays`, `DeadRetentionDays`, `PurgeIntervalHours`, `PurgeBatchSize`. `OutboxDrainer<TRow>` gains an optional 8th constructor parameter `IOutboxPurgeDialect<TRow>? purgeDialect = null`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Messaging.Tests/Outbox/OutboxDrainerPurgeTests.cs`:

```csharp
using System.Data.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Tests.Outbox;

public class OutboxDrainerPurgeTests
{
    private sealed record Row(Guid Id, int Attempts) : IClaimedRow;

    // A dialect that claims nothing, so DrainOnceAsync exercises only the purge decision.
    private sealed class EmptyDialect : IOutboxDialect<Row>
    {
        public DbConnection CreateConnection() => new FakeConnection();

        public Task<IReadOnlyList<Row>> ClaimAsync(
            DbConnection connection, string leaseOwner, DateTimeOffset now,
            DateTimeOffset leaseExpiresAt, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Row>>([]);

        public Task CompleteAsync(DbConnection c, Guid id, DateTimeOffset at, CancellationToken ct)
            => Task.CompletedTask;

        public Task FailAsync(DbConnection c, Guid id, int attempts, DateTimeOffset next,
            bool dead, string error, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingPurgeDialect : IOutboxPurgeDialect<Row>
    {
        public int SentCalls { get; private set; }
        public int DeadCalls { get; private set; }
        public DateTimeOffset LastSentOlderThan { get; private set; }

        public Task<int> PurgeSentAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            SentCalls++;
            LastSentOlderThan = olderThan;
            return Task.FromResult(0); // nothing left to delete — loop terminates
        }

        public Task<int> PurgeDeadAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            DeadCalls++;
            return Task.FromResult(0);
        }
    }

    private sealed class NoopDispatcher : IOutboxDispatcher<Row>
    {
        public Task<DispatchResult> DispatchAsync(IServiceProvider sp, Row row, CancellationToken ct)
            => Task.FromResult(DispatchResult.Delivered());
    }

    private static OutboxDrainer<Row> Build(
        OutboxDrainerOptions<Row> options, IOutboxPurgeDialect<Row>? purge, TimeProvider time)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new OutboxDrainer<Row>(
            new EmptyDialect(),
            new NoopDispatcher(),
            new DrainSignal(),
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            time,
            NullLogger<OutboxDrainer<Row>>.Instance,
            purge);
    }

    [Fact]
    public async Task DrainOnce_ShouldNotPurge_WhenPurgeDisabled()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = false };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(0, purge.SentCalls);
        Assert.Equal(0, purge.DeadCalls);
    }

    [Fact]
    public async Task DrainOnce_ShouldPurge_OnFirstCycle_WhenEnabled()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, purge.SentCalls);
        Assert.Equal(1, purge.DeadCalls);
    }

    // The purge is interval-gated: a second cycle moments later must not re-run it.
    [Fact]
    public async Task DrainOnce_ShouldNotPurgeTwice_WithinTheInterval()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true, PurgeIntervalHours = 24 };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, purge.SentCalls);
    }

    [Fact]
    public async Task DrainOnce_ShouldNotThrow_WhenNoPurgeDialectRegistered()
    {
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true };

        var drainer = Build(options, purge: null, TimeProvider.System);
        var exception = await Record.ExceptionAsync(() => drainer.DrainOnceAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    // Retention is expressed in days and must be subtracted from the drainer's clock, not DateTime.UtcNow.
    [Fact]
    public async Task DrainOnce_ShouldComputeSentCutoff_FromRetentionDays()
    {
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true, SentRetentionDays = 7 };

        var drainer = Build(options, purge, new FixedTimeProvider(now));
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(now.AddDays(-7), purge.LastSentOlderThan);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Minimal DbConnection stand-in: the drainer opens it but the fake dialects never use it.
    private sealed class FakeConnection : DbConnection
    {
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel il) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Messaging.Tests/Themia.Messaging.Tests.csproj`
Expected: FAIL — `IOutboxPurgeDialect<>` does not exist, `OutboxDrainerOptions<Row>` has no `PurgeEnabled`, and `OutboxDrainer<Row>` has no 8-parameter constructor.

- [ ] **Step 3: Create the purge contracts**

Create `src/neutral/Themia.Messaging/Outbox/IOutboxPurgeDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Messaging.Outbox;

/// <summary>
/// Engine-specific deletion of terminal outbox rows. Separate from <see cref="IOutboxDialect{TRow}"/> so an
/// outbox can be drained without granting it delete authority, and generic over the row type so several
/// outboxes can be purged independently in one container.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape identifying which outbox this purges.</typeparam>
public interface IOutboxPurgeDialect<TRow>
    where TRow : IClaimedRow
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> successfully-sent rows older than
    /// <paramref name="olderThan"/>. Batched deliberately: an unbounded DELETE on a large outbox holds
    /// long locks and bloats the table, so the caller loops until this returns 0.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Rows sent before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; 0 means nothing is left.</returns>
    Task<int> PurgeSentAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> dead-lettered rows older than <paramref name="olderThan"/>.
    /// Kept on a longer window than sent rows: each dead row is an unresolved delivery failure.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Rows that died before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; 0 means nothing is left.</returns>
    Task<int> PurgeDeadAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);
}
```

Create `src/neutral/Themia.Messaging/Inbox/IInboxPurgeDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Messaging.Inbox;

/// <summary>
/// Engine-specific deletion of expired inbox admission records. Deliberately separate from the outbox
/// purge contract: Notifications implements an outbox purge but has no inbox and must not be forced to
/// stub one.
/// </summary>
public interface IInboxPurgeDialect
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> admission records received before
    /// <paramref name="olderThan"/>. Batched; the caller loops until this returns 0.
    /// </summary>
    /// <remarks>
    /// Forgetting an admission record means a redelivery older than the window is processed as new. The
    /// window must therefore exceed the maximum age of any redelivery the sending outbox can produce.
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="olderThan">Records received before this instant are eligible.</param>
    /// <param name="batchSize">The maximum number of rows to delete in one statement.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted; 0 means nothing is left.</returns>
    Task<int> PurgeAdmittedAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct);
}
```

- [ ] **Step 4: Add purge settings to the drainer options**

In `src/neutral/Themia.Messaging/Outbox/OutboxDrainerOptions.cs`, append these properties inside the class:

```csharp
    /// <summary>
    /// Whether the drain loop also purges terminal rows. Defaults to <see langword="false"/> so that
    /// enabling retention is always a deliberate act: switching it on for an existing deployment deletes
    /// history on the first run, which must never arrive as a side effect of a version bump.
    /// </summary>
    public bool PurgeEnabled { get; set; }

    /// <summary>How long successfully-sent rows are kept. Default 7 days.</summary>
    public int SentRetentionDays { get; set; } = 7;

    /// <summary>How long dead-lettered rows are kept. Default 90 days — each one is an unresolved failure.</summary>
    public int DeadRetentionDays { get; set; } = 90;

    /// <summary>Minimum interval between purge passes. Default 24 hours.</summary>
    public int PurgeIntervalHours { get; set; } = 24;

    /// <summary>Rows deleted per statement. Default 1000, keeping each delete's lock hold short.</summary>
    public int PurgeBatchSize { get; set; } = 1000;
```

- [ ] **Step 5: Wire the purge into the drain loop**

In `src/neutral/Themia.Messaging/Outbox/OutboxDrainer.cs`, add the optional parameter to the primary constructor — append it after `logger`:

```csharp
    ILogger<OutboxDrainer<TRow>> logger,
    IOutboxPurgeDialect<TRow>? purgeDialect = null) : BackgroundService
```

Add the matching doc line above the class, after the `logger` param doc:

```csharp
/// <param name="purgeDialect">Optional retention purge; when absent, no purge runs regardless of options.</param>
```

Add a field next to `leaseOwner`:

```csharp
    private DateTimeOffset lastPurgeAt = DateTimeOffset.MinValue;
```

In `DrainOnceAsync`, replace the early return with a version that still purges, and add the purge call before the method returns. The full method body after the claim becomes:

```csharp
        var claimed = await dialect.ClaimAsync(connection, leaseOwner, now, leaseExpires, options.MaxBatchSize, ct).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            await PurgeIfDueAsync(connection, now, ct).ConfigureAwait(false);
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeliverAsync(scope.ServiceProvider, connection, row, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown — abort cleanly
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox row {Id} could not be finalized; leaving for lease re-claim.", row.Id);
            }
        }

        await PurgeIfDueAsync(connection, now, ct).ConfigureAwait(false);
        return claimed.Count;
```

Add the private method:

```csharp
    // Retention runs on the drain loop's own connection and cadence: a dedicated scheduler would force a
    // new package dependency on every adopter purely to delete rows on a timer.
    private async Task PurgeIfDueAsync(DbConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.PurgeEnabled || purgeDialect is null)
        {
            return;
        }

        if (now - lastPurgeAt < TimeSpan.FromHours(options.PurgeIntervalHours))
        {
            return;
        }

        lastPurgeAt = now;

        var sentDeleted = await PurgeAllAsync(
            (c, cutoff, batch, token) => purgeDialect.PurgeSentAsync(c, cutoff, batch, token),
            connection, now.AddDays(-options.SentRetentionDays), ct).ConfigureAwait(false);

        var deadDeleted = await PurgeAllAsync(
            (c, cutoff, batch, token) => purgeDialect.PurgeDeadAsync(c, cutoff, batch, token),
            connection, now.AddDays(-options.DeadRetentionDays), ct).ConfigureAwait(false);

        if (sentDeleted + deadDeleted > 0)
        {
            logger.LogInformation(
                "Outbox purge removed {SentDeleted} sent and {DeadDeleted} dead rows.", sentDeleted, deadDeleted);
        }
    }

    private async Task<int> PurgeAllAsync(
        Func<DbConnection, DateTimeOffset, int, CancellationToken, Task<int>> purge,
        DbConnection connection,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            ct.ThrowIfCancellationRequested();
            deleted = await purge(connection, cutoff, options.PurgeBatchSize, ct).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == options.PurgeBatchSize);

        return total;
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Messaging.Tests/Themia.Messaging.Tests.csproj`
Expected: PASS — 20 tests (15 existing + 5 new).

- [ ] **Step 7: Declare the new public API**

Run: `dotnet build src/neutral/Themia.Messaging/Themia.Messaging.csproj --no-incremental`
Read every `RS0016` line and append the exact signature it names to `src/neutral/Themia.Messaging/PublicAPI.Unshipped.txt`. The new entries are the two interfaces, their methods, the five `OutboxDrainerOptions<TRow>` property get/set pairs, and the replaced `OutboxDrainer<TRow>` constructor (the old 7-parameter entry must be **removed**, since the constructor signature changed).

Re-run the build until it reports `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Messaging tests/Themia.Messaging.Tests
git commit -m "feat(messaging): add purge contracts and run retention from the drain loop"
```

---

### Task 2: Messaging schema migration and module scaffold

**Files:**
- Create: `src/modules/Themia.Modules.Messaging/Themia.Modules.Messaging.csproj`
- Create: `src/modules/Themia.Modules.Messaging/Entities/MessageOutboxEntry.cs`
- Create: `src/modules/Themia.Modules.Messaging/Entities/OutboxStatus.cs`
- Create: `src/modules/Themia.Modules.Messaging/Migrations/MessagingSchemaMigration.cs`
- Create: `src/modules/Themia.Modules.Messaging/MessagingModuleOptions.cs`
- Create: `src/modules/Themia.Modules.Messaging/PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`, `MessagingModuleOptionsTests.cs`

**Interfaces:**
- Consumes: `Themia.Framework.Core.Abstractions.Entities.Entity<Guid>`, `Themia.Framework.Core.Abstractions.Tenancy.ITenantEntity`, `TenantId`.
- Produces: `MessageOutboxEntry` (properties `MessageId`, `TenantId`, `Type`, `Payload`, `Destination`, `Origin`, `EntityKey`, `Version`, `Headers`, `Status`, `Attempts`, `NextAttemptAt`, `ScheduledFor`, `LeaseOwner`, `LeaseExpiresAt`, `CreatedAt`, `SentAt`, `LastError`, method `SetId(Guid)`); `OutboxStatus` enum; `MessagingSchemaMigration`; `MessagingModuleOptions` with `Validate()`.

- [ ] **Step 1: Create the project file**

Create `src/modules/Themia.Modules.Messaging/Themia.Modules.Messaging.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Messaging</PackageId>
    <Description>Tenant-aware inter-service messaging — transactional outbox over the shared Themia.Messaging drainer, deduplicating inbox, and retention purge. FluentMigrator schema (PostgreSQL + MySQL + SQL Server).</Description>
    <PackageTags>themia;messaging;outbox;inbox;integration;efcore;dapper</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../neutral/Themia.Messaging/Themia.Messaging.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Messaging.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Messaging.IntegrationTests" />
  </ItemGroup>
</Project>
```

Create the two PublicAPI files:

```bash
: > src/modules/Themia.Modules.Messaging/PublicAPI.Shipped.txt
printf '#nullable enable\n' > src/modules/Themia.Modules.Messaging/PublicAPI.Unshipped.txt
dotnet sln Themia.sln add src/modules/Themia.Modules.Messaging/Themia.Modules.Messaging.csproj --solution-folder modules
```

- [ ] **Step 2: Create the entity and status enum**

Create `src/modules/Themia.Modules.Messaging/Entities/OutboxStatus.cs`:

```csharp
namespace Themia.Modules.Messaging.Entities;

/// <summary>Lifecycle state of an outbox row. Values are persisted as integers and must not be renumbered.</summary>
public enum OutboxStatus
{
    /// <summary>Awaiting its first delivery attempt.</summary>
    Pending = 0,

    /// <summary>Claimed by a drainer under a lease.</summary>
    Sending = 1,

    /// <summary>Delivered.</summary>
    Sent = 2,

    /// <summary>Failed and eligible for another attempt after backoff.</summary>
    Failed = 3,

    /// <summary>Permanently undeliverable; no further attempts.</summary>
    Dead = 4,
}
```

Create `src/modules/Themia.Modules.Messaging/Entities/MessageOutboxEntry.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Messaging.Entities;

/// <summary>
/// A message staged for delivery to another service. The persisted form of
/// <see cref="Themia.Messaging.Messages.MessageEnvelope"/>; <see cref="Payload"/> stays opaque and is never
/// deserialized by the framework.
/// </summary>
public sealed class MessageOutboxEntry : Entity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>Stable identifier the receiver deduplicates on; never reassigned across retries.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The logical message type the receiver routes on.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The serialized body, carried verbatim.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The logical peer this is addressed to.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>The system that originated the message — not the last hop.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The key the receiver's own staleness fence applies within, if any.</summary>
    public string? EntityKey { get; set; }

    /// <summary>A monotonic version for <see cref="EntityKey"/>, carried for the receiver's fence.</summary>
    public long? Version { get; set; }

    /// <summary>Extra transport metadata as JSON. Never contains credentials.</summary>
    public string? Headers { get; set; }

    /// <summary>Lifecycle state.</summary>
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>Number of delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the message may be (re)attempted.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>If set, the message is held until this time.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Identifier of the drainer instance currently holding the row.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires; a past value on a sending row is reclaimable.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the row was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the message was successfully delivered.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The last failure message, if any. Never contains credentials.</summary>
    public string? LastError { get; set; }

    /// <summary>Assigns the identifier for a new (transient) row.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 3: Write the failing options test**

Create `tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Messaging/Themia.Modules.Messaging.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/Themia.Modules.Messaging.Tests/MessagingModuleOptionsTests.cs`:

```csharp
using Themia.Modules.Messaging;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

public class MessagingModuleOptionsTests
{
    [Fact]
    public void Validate_ShouldSucceed_WithDefaults()
    {
        var exception = Record.Exception(() => new MessagingModuleOptions().Validate());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenConnectionStringNameIsMissing(string? name)
    {
        var options = new MessagingModuleOptions { ConnectionStringName = name! };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldThrow_WhenMaxBatchSizeIsNotPositive(int value)
    {
        var options = new MessagingModuleOptions { MaxBatchSize = value };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    // Origin identifies this service to every peer; a blank origin makes forwarded messages un-droppable
    // by the loop guard, so it is rejected rather than defaulted.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenOriginIsMissing(string? origin)
    {
        var options = new MessagingModuleOptions { Origin = origin! };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    // The inbox window must outlast any redelivery the outbox can produce, or a late redelivery is
    // processed as new. Reject the configuration that guarantees that failure.
    [Fact]
    public void Validate_ShouldThrow_WhenInboxRetentionIsShorterThanDeadRetention()
    {
        var options = new MessagingModuleOptions { InboxRetentionDays = 5, DeadRetentionDays = 90 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
```

Register the project:

```bash
dotnet sln Themia.sln add tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj --solution-folder tests
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`
Expected: FAIL — `MessagingModuleOptions` does not exist.

- [ ] **Step 5: Create the options**

Create `src/modules/Themia.Modules.Messaging/MessagingModuleOptions.cs`:

```csharp
namespace Themia.Modules.Messaging;

/// <summary>Configuration for the Themia Messaging module.</summary>
public sealed class MessagingModuleOptions
{
    /// <summary>Name of the connection string (in <c>ConnectionStrings</c>) the module migrates and drains.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>
    /// This service's identity, stamped on every published message as its origin and used by the receiver
    /// to drop messages that arrive back where they started.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>How often the drainer polls when no in-process signal arrives. Default 5s.</summary>
    public int DrainIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum outbox rows claimed per drain cycle. Default 50.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Attempts before a message is marked dead. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed row's lease is held before it is reclaimable. Default 120s.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Whether retention purging runs. Defaults to <see langword="true"/>: this schema is new, so
    /// there is no pre-existing history that enabling it could destroy.</summary>
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>How long delivered rows are kept. Default 7 days.</summary>
    public int SentRetentionDays { get; set; } = 7;

    /// <summary>How long dead-lettered rows are kept. Default 90 days.</summary>
    public int DeadRetentionDays { get; set; } = 90;

    /// <summary>How long inbox admission records are kept. Default 30 days.</summary>
    public int InboxRetentionDays { get; set; } = 30;

    /// <summary>Validates the options, throwing if any value is out of range or inconsistent.</summary>
    /// <exception cref="ArgumentException">A required string is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        if (string.IsNullOrWhiteSpace(Origin))
            throw new ArgumentException("Must not be null or whitespace.", nameof(Origin));
        if (DrainIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(DrainIntervalSeconds), DrainIntervalSeconds, "Must be at least 1 second.");
        if (MaxBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), MaxBatchSize, "Must be at least 1.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Must be at least 1.");
        if (LeaseSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(LeaseSeconds), LeaseSeconds, "Must be at least 1 second.");
        if (SentRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(SentRetentionDays), SentRetentionDays, "Must be at least 1 day.");
        if (DeadRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(DeadRetentionDays), DeadRetentionDays, "Must be at least 1 day.");

        // Forgetting an admission record before the sender can stop retrying means a late redelivery is
        // reprocessed as new. Dead-lettering bounds how long the sender keeps trying, so the inbox window
        // must cover it.
        if (InboxRetentionDays < DeadRetentionDays)
            throw new ArgumentOutOfRangeException(
                nameof(InboxRetentionDays),
                InboxRetentionDays,
                $"Must be at least {nameof(DeadRetentionDays)} ({DeadRetentionDays}) so a redelivery cannot outlive its admission record.");
    }
}
```

Note the test uses `InboxRetentionDays = 5` against the default `DeadRetentionDays = 90`, so it trips this check. The default 30/90 pair would *also* trip it — so change the `InboxRetentionDays` default to `90`:

```csharp
    /// <summary>How long inbox admission records are kept. Default 90 days, matching the dead-letter window
    /// so a redelivery can never outlive its admission record.</summary>
    public int InboxRetentionDays { get; set; } = 90;
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`
Expected: PASS — 8 tests.

- [ ] **Step 7: Create the schema migration**

Create `src/modules/Themia.Modules.Messaging/Migrations/MessagingSchemaMigration.cs`:

```csharp
using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Messaging.Migrations;

/// <summary>Creates the <c>messaging</c> schema and its two tables (<c>outbox_messages</c>,
/// <c>inbox_messages</c>) on PostgreSQL, MySQL, and SQL Server. FluentMigrator is the single DDL
/// authority for both the EF and Dapper data layers (DECISION #6).</summary>
[Migration(202607310001, "Themia.Messaging: create messaging schema and tables")]
public sealed class MessagingSchemaMigration : Migration
{
    private const string SchemaName = "messaging";

    /// <summary>Maps a datetime column to the engine-appropriate type. MySQL's FluentMigrator generator
    /// does not support <c>DateTimeOffset</c>, so MySQL uses <c>DATETIME(6)</c> while PostgreSQL and SQL
    /// Server use <c>datetimeoffset</c>, preserving timezone fidelity for the lease and scheduling columns.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        Create.Schema(SchemaName);

        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase("postgresql").Delegate(() => CreateIndexes("\"messaging\".\"outbox_messages\""));
        IfDatabase("sqlserver").Delegate(() => CreateIndexes("[messaging].[outbox_messages]"));
        IfDatabase("mysql").Delegate(() => CreateIndexes("outbox_messages"));
    }

    private void CreateTables(DateTimeType dt)
    {
        // Operational outbox row — not soft-deletable (purged, not tombstoned; the purge is implemented).
        var outbox = Create.Table("outbox_messages").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("type").AsString(200).NotNullable()
            .WithColumn("payload").AsString(int.MaxValue).NotNullable()
            .WithColumn("destination").AsString(100).NotNullable()
            .WithColumn("origin").AsString(100).NotNullable()
            .WithColumn("entity_key").AsString(200).Nullable()
            .WithColumn("version").AsInt64().Nullable()
            .WithColumn("headers").AsString(int.MaxValue).Nullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable();
        dt(outbox.WithColumn("next_attempt_at")).NotNullable();
        dt(outbox.WithColumn("scheduled_for")).Nullable();
        outbox.WithColumn("lease_owner").AsString(100).Nullable();
        dt(outbox.WithColumn("lease_expires_at")).Nullable();
        dt(outbox.WithColumn("created_at")).NotNullable();
        dt(outbox.WithColumn("sent_at")).Nullable();
        outbox.WithColumn("last_error").AsString(int.MaxValue).Nullable();

        Create.Index("ix_msg_outbox_tenant").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();

        // The same logical message fanned out to two peers legitimately shares a message_id — each
        // receiver dedups on (origin, message_id) independently — but enqueuing it twice for the SAME
        // destination is a double-publish bug, caught here rather than at the far end.
        Create.Index("ux_msg_outbox_message_destination").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("message_id").Ascending().OnColumn("destination").Ascending()
            .WithOptions().Unique();

        // Admission records. The composite PK IS the deduplication guarantee.
        var inbox = Create.Table("inbox_messages").InSchema(SchemaName)
            .WithColumn("origin").AsString(100).NotNullable().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("type").AsString(200).NotNullable();
        dt(inbox.WithColumn("received_at")).NotNullable();

        Create.Index("ix_msg_inbox_received").OnTable("inbox_messages").InSchema(SchemaName)
            .OnColumn("received_at").Ascending();
    }

    /// <summary>Creates the composite indexes the claim and purge queries scan.
    /// <paramref name="table"/> is the engine-quoted, schema-qualified identifier — no user input is
    /// interpolated, only the fixed identifier.</summary>
    private void CreateIndexes(string table)
    {
        Execute.Sql($"CREATE INDEX ix_msg_outbox_claim ON {table} (status, next_attempt_at);");
        Execute.Sql($"CREATE INDEX ix_msg_outbox_purge ON {table} (status, sent_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("inbox_messages").InSchema(SchemaName);
        Delete.Table("outbox_messages").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
```

- [ ] **Step 8: Build and declare the public API**

Run: `dotnet build src/modules/Themia.Modules.Messaging/Themia.Modules.Messaging.csproj --no-incremental`
Append every symbol named by an `RS0016` error to `PublicAPI.Unshipped.txt`. Re-run until `Build succeeded.`

- [ ] **Step 9: Commit**

```bash
git add src/modules/Themia.Modules.Messaging tests/Themia.Modules.Messaging.Tests Themia.sln
git commit -m "feat(messaging): add module scaffold, outbox entity and messaging schema migration"
```

---

### Task 3: Outbox store, mappings, and DI

**Files:**
- Create: `src/modules/Themia.Modules.Messaging/Stores/MessageOutboxStore.cs`
- Create: `src/modules/Themia.Modules.Messaging/Mapping/MessagingDapperMappings.cs`
- Create: `src/modules/Themia.Modules.Messaging/EntityConfiguration/MessageOutboxEntryConfiguration.cs`
- Create: `src/modules/Themia.Modules.Messaging/MessagingModule.cs`
- Create: `src/modules/Themia.Modules.Messaging/DependencyInjection/MessagingServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Messaging.Tests/Stores/MessageOutboxStoreTests.cs`

**Interfaces:**
- Consumes: `IMessageOutboxStore.EnqueueAsync(MessageEnvelope, CancellationToken)`, `MessageEnvelope.Validate()`, `IRepository<MessageOutboxEntry, Guid>`, `MessageOutboxEntry`, `OutboxStatus`, `MessagingModuleOptions`, `EntityMappingRegistry`, `DrainSignal`, `OutboxDrainer<ClaimedMessageRow>`, `OutboxDrainerOptions<ClaimedMessageRow>`, `IOutboxDialect<ClaimedMessageRow>`, `IOutboxDispatcher<ClaimedMessageRow>`.
- Produces: `MessageOutboxStore : IMessageOutboxStore`; `MessagingDapperMappings.Apply(EntityMappingRegistry)`; `MessagingServiceCollectionExtensions.AddThemiaMessagingModule(IServiceCollection, Action<MessagingModuleOptions>?)`; `MessagingModule`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Modules.Messaging.Tests/Stores/MessageOutboxStoreTests.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Messaging.Messages;
using Themia.Modules.Messaging.Entities;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.Tests.Stores;

public class MessageOutboxStoreTests
{
    private sealed class RecordingRepository : IRepository<MessageOutboxEntry, Guid>
    {
        public List<MessageOutboxEntry> Added { get; } = [];

        public Task AddAsync(MessageOutboxEntry entity, CancellationToken ct = default)
        {
            Added.Add(entity);
            return Task.CompletedTask;
        }

        // The store only ever calls AddAsync; the rest of IRepository is not exercised.
        public Task<MessageOutboxEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(MessageOutboxEntry entity, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(MessageOutboxEntry entity, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static MessageEnvelope Valid() => new()
    {
        MessageId = Guid.CreateVersion7(),
        Type = "listing.snapshot.v1",
        Payload = """{"id":42}""",
        Destination = "propertiezy",
        Origin = "ezy-assets",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task EnqueueAsync_ShouldStageOneRow_WithPendingStatusAndZeroAttempts()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(OutboxStatus.Pending, entry.Status);
        Assert.Equal(0, entry.Attempts);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldCarryEnvelopeFieldsVerbatim()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();

        await store.EnqueueAsync(envelope, CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(envelope.MessageId, entry.MessageId);
        Assert.Equal(envelope.Type, entry.Type);
        Assert.Equal(envelope.Payload, entry.Payload);
        Assert.Equal(envelope.Destination, entry.Destination);
        Assert.Equal(envelope.Origin, entry.Origin);
    }

    // A row must be due immediately unless the caller scheduled it, or it would never be claimed.
    [Fact]
    public async Task EnqueueAsync_ShouldSetNextAttemptAt_ToNow_WhenNotScheduled()
    {
        var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, new FixedTimeProvider(now));

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal(now, entry.NextAttemptAt);
        Assert.Null(entry.ScheduledFor);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldSerializeHeaders_AsJson()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();
        envelope.Headers = new Dictionary<string, string> { ["x-trace"] = "abc" };

        await store.EnqueueAsync(envelope, CancellationToken.None);

        var entry = Assert.Single(repository.Added);
        Assert.Equal("""{"x-trace":"abc"}""", entry.Headers);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldLeaveHeadersNull_WhenNoneSupplied()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);

        await store.EnqueueAsync(Valid(), CancellationToken.None);

        Assert.Null(Assert.Single(repository.Added).Headers);
    }

    // Validation runs at enqueue so a malformed message fails at the call site, not hours later in the drainer.
    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsInvalid()
    {
        var repository = new RecordingRepository();
        var store = new MessageOutboxStore(repository, TimeProvider.System);
        var envelope = Valid();
        envelope.Origin = string.Empty;

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(envelope, CancellationToken.None));
        Assert.Empty(repository.Added);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldThrow_WhenEnvelopeIsNull()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => new MessageOutboxStore(new RecordingRepository(), TimeProvider.System)
                .EnqueueAsync(null!, CancellationToken.None));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`
Expected: FAIL — `MessageOutboxStore` does not exist.

**If `IRepository<T,TKey>` has members beyond the four stubbed above**, the fake will not compile. Open `src/framework/Themia.Framework.Data.Abstractions/Repositories/IRepository.cs`, and add a `throw new NotSupportedException();` stub for each additional member — the store only calls `AddAsync`.

- [ ] **Step 3: Implement the store**

Create `src/modules/Themia.Modules.Messaging/Stores/MessageOutboxStore.cs`:

```csharp
using System.Text.Json;

using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Messaging.Messages;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.Stores;

/// <summary>Repository-backed <see cref="IMessageOutboxStore"/>. Peer-agnostic: the framework binds the
/// injected repository to EF or Dapper. The repository stamps the tenant on insert; the caller's unit of
/// work commits, so a published message can never survive a rolled-back transaction.</summary>
internal sealed class MessageOutboxStore(
    IRepository<MessageOutboxEntry, Guid> repository,
    TimeProvider time) : IMessageOutboxStore
{
    /// <inheritdoc />
    public Task EnqueueAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Validate here so a malformed message fails at the call site rather than hours later in the drainer.
        message.Validate();

        var now = time.GetUtcNow();
        var entry = new MessageOutboxEntry
        {
            MessageId = message.MessageId,
            Type = message.Type,
            Payload = message.Payload,
            Destination = message.Destination,
            Origin = message.Origin,
            EntityKey = message.EntityKey,
            Version = message.Version,
            Headers = message.Headers is null ? null : JsonSerializer.Serialize(message.Headers),
            Status = OutboxStatus.Pending,
            Attempts = 0,
            ScheduledFor = message.ScheduledFor,
            NextAttemptAt = message.ScheduledFor ?? now,
            CreatedAt = message.CreatedAt == default ? now : message.CreatedAt,
        };
        entry.SetId(Guid.CreateVersion7());

        return repository.AddAsync(entry, ct);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`
Expected: PASS — 15 tests.

- [ ] **Step 5: Add the Dapper mappings and EF configuration**

Create `src/modules/Themia.Modules.Messaging/Mapping/MessagingDapperMappings.cs`:

```csharp
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.Mapping;

/// <summary>Registers the Themia Messaging entity mappings (schema-qualified <c>messaging.*</c> table
/// names) into a Dapper <see cref="EntityMappingRegistry"/>, so the Dapper peer reads and writes the exact
/// same columns as the EF peer over the FluentMigrator-owned schema.</summary>
public static class MessagingDapperMappings
{
    /// <summary>Registers the Messaging entity mappings. Columns follow the snake_case convention, which
    /// matches the EF config and the migration one-for-one.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<MessageOutboxEntry>(
            EntityMapping.ForConvention<MessageOutboxEntry>("messaging.outbox_messages", null));
    }
}
```

Create `src/modules/Themia.Modules.Messaging/EntityConfiguration/MessageOutboxEntryConfiguration.cs` by copying the shape of `src/modules/Themia.Modules.Notifications/EntityConfiguration/` — open that folder, read the existing `OutboxMessage` configuration, and mirror it for `MessageOutboxEntry` against table `outbox_messages` in schema `messaging`, mapping every property to its snake_case column.

- [ ] **Step 6: Add the module class and DI**

Create `src/modules/Themia.Modules.Messaging/MessagingModule.cs` by mirroring `src/modules/Themia.Modules.Notifications/NotificationsModule.cs`: constructor taking `MigrationEngine` (and an overload taking `MessagingModuleOptions`), a `Descriptor` naming `Themia.Messaging`, and an `InitializeAsync` that runs `ThemiaMigrations.Run(engine, connectionString, typeof(MessagingSchemaMigration).Assembly)`.

Create `src/modules/Themia.Modules.Messaging/DependencyInjection/MessagingServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Framework.Data.Dapper.Mapping;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Mapping;
using Themia.Modules.Messaging.Stores;

namespace Themia.Modules.Messaging.DependencyInjection;

/// <summary>Registers the Themia Messaging module services (outbox store, drainer, retention).</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Messaging module's own services: the peer-agnostic outbox store, the
    /// <see cref="DrainSignal"/>, and the shared <c>OutboxDrainer</c> hosted service. The adopter must ALSO
    /// register a provider dialect via <c>AddThemiaMessaging{PostgreSql|MySql|SqlServer}(...)</c>, an
    /// <see cref="IOutboxDispatcher{TRow}"/> that delivers messages, and a framework data peer; then run
    /// <c>MessagingModule.InitializeAsync</c> to apply the schema migration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="MessagingModuleOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddThemiaMessagingModule(
        this IServiceCollection services,
        Action<MessagingModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MessagingModuleOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<DrainSignal>();

        services.TryAddSingleton(new OutboxDrainerOptions<ClaimedMessageRow>
        {
            DrainIntervalSeconds = options.DrainIntervalSeconds,
            MaxBatchSize = options.MaxBatchSize,
            MaxAttempts = options.MaxAttempts,
            LeaseSeconds = options.LeaseSeconds,
            PurgeEnabled = options.PurgeEnabled,
            SentRetentionDays = options.SentRetentionDays,
            DeadRetentionDays = options.DeadRetentionDays,
        });

        services.TryAddScoped<IMessageOutboxStore, MessageOutboxStore>();

        ContributeDapperMappings(services);
        services.AddHostedService<OutboxDrainer<ClaimedMessageRow>>();

        return services;
    }

    // Mirror Notifications: scan the collection for the already-registered EntityMappingRegistry singleton
    // instance and apply the Messaging mappings to it. No service provider is built. No-op when EF is the peer.
    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                MessagingDapperMappings.Apply(registry);
                return;
            }
        }
    }
}
```

- [ ] **Step 7: Build, declare public API, and run all tests**

Run: `dotnet build Themia.sln --no-incremental` — append RS0016 symbols to `PublicAPI.Unshipped.txt` until it succeeds.
Run: `dotnet test tests/Themia.Modules.Messaging.Tests/Themia.Modules.Messaging.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/modules/Themia.Modules.Messaging tests/Themia.Modules.Messaging.Tests
git commit -m "feat(messaging): add repository-backed outbox store, mappings and module DI"
```

---

### Task 4: PostgreSQL dialect — claim, complete, fail, purge

**Files:**
- Create: `src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj`
- Create: `src/neutral/Themia.Messaging.PostgreSql/PostgresMessagingDialect.cs`
- Create: `src/neutral/Themia.Messaging.PostgreSql/PostgresMessagingPurgeDialect.cs`
- Create: `src/neutral/Themia.Messaging.PostgreSql/ServiceCollectionExtensions.cs`
- Create: `src/neutral/Themia.Messaging.PostgreSql/PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Modules.Messaging.IntegrationTests/` (project + `OutboxRoundTripTests.cs`)

**Interfaces:**
- Consumes: `IOutboxDialect<ClaimedMessageRow>`, `IOutboxPurgeDialect<ClaimedMessageRow>`, `IInboxPurgeDialect`, `ClaimedMessageRow`.
- Produces: `AddThemiaMessagingPostgreSql(IServiceCollection, string connectionStringName = "Default")` registering both dialects.

- [ ] **Step 1: Create the project**

Create `src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj`, copying `src/modules/Themia.Modules.Notifications.PostgreSql/*.csproj` and changing `PackageId` to `Themia.Messaging.PostgreSql`, the `Description` to describe the messaging outbox dialect, and the `ProjectReference` to `../Themia.Messaging/Themia.Messaging.csproj`. Keep `TargetFramework` at `net10.0`.

```bash
: > src/neutral/Themia.Messaging.PostgreSql/PublicAPI.Shipped.txt
printf '#nullable enable\n' > src/neutral/Themia.Messaging.PostgreSql/PublicAPI.Unshipped.txt
dotnet sln Themia.sln add src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj --solution-folder neutral
```

- [ ] **Step 2: Implement the claim dialect**

Create `src/neutral/Themia.Messaging.PostgreSql/PostgresMessagingDialect.cs`:

```csharp
using System.Data.Common;

using Dapper;
using Npgsql;

using Themia.Messaging.Outbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IOutboxDialect{TRow}"/> for the messaging outbox
/// (Npgsql). Claims due rows with <c>FOR UPDATE SKIP LOCKED</c> so concurrent drainers never collide.</summary>
internal sealed class PostgresMessagingDialect(string connectionString) : IOutboxDialect<ClaimedMessageRow>
{
    // status: 0 pending, 1 sending, 2 sent, 3 failed, 4 dead (matches OutboxStatus).
    private const string SelectDueSql = """
        SELECT id FROM messaging.outbox_messages
        WHERE next_attempt_at <= @now
          AND (scheduled_for IS NULL OR scheduled_for <= @now)
          AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
        ORDER BY next_attempt_at
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimSql = """
        UPDATE messaging.outbox_messages
        SET status = 1, lease_owner = @owner, lease_expires_at = @exp
        WHERE id = ANY(@ids)
        RETURNING id, message_id, tenant_id, type, payload, destination, origin, entity_key, version, attempts
        """;

    private const string CompleteSql = """
        UPDATE messaging.outbox_messages
        SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    private const string FailSql = """
        UPDATE messaging.outbox_messages
        SET status = @status, attempts = @attempts, next_attempt_at = @next,
            last_error = @error, lease_owner = NULL, lease_expires_at = NULL
        WHERE id = @id
        """;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedMessageRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);

        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            SelectDueSql, new { now, batch = batchSize }, tx, cancellationToken: ct))).ToArray();

        if (ids.Length == 0)
        {
            await tx.CommitAsync(ct);
            return [];
        }

        var rows = await connection.QueryAsync<ClaimedMessageRow>(new CommandDefinition(
            ClaimSql, new { owner = leaseOwner, exp = leaseExpiresAt, ids }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return rows.ToArray();
    }

    /// <inheritdoc />
    public Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset completedAt, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            CompleteSql, new { id, sentAt = completedAt }, cancellationToken: ct));

    /// <inheritdoc />
    public Task FailAsync(
        DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            FailSql,
            new { id, status = dead ? 4 : 3, attempts, next = nextAttemptAt, error },
            cancellationToken: ct));
}
```

**Note on the `ClaimedMessageRow` mapping:** Dapper maps `RETURNING` columns to the positional record parameters by name. `message_id → MessageId`, `tenant_id → TenantId`, `entity_key → EntityKey` require `DefaultTypeMap.MatchNamesWithUnderscores = true`. Set it once in `ServiceCollectionExtensions` (Step 4) rather than per-query.

- [ ] **Step 3: Implement the purge dialect**

Create `src/neutral/Themia.Messaging.PostgreSql/PostgresMessagingPurgeDialect.cs`:

```csharp
using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>PostgreSQL retention deletes for the messaging outbox and inbox. Every statement is bounded by
/// <c>LIMIT</c> via a <c>ctid</c> subquery: an unbounded DELETE on a large table holds long locks and
/// bloats it, so the caller loops until a batch comes back short.</summary>
internal sealed class PostgresMessagingPurgeDialect
    : IOutboxPurgeDialect<ClaimedMessageRow>, IInboxPurgeDialect
{
    private const string PurgeSentSql = """
        DELETE FROM messaging.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM messaging.outbox_messages
            WHERE status = 2 AND sent_at < @olderThan
            LIMIT @batch
        )
        """;

    private const string PurgeDeadSql = """
        DELETE FROM messaging.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM messaging.outbox_messages
            WHERE status = 4 AND next_attempt_at < @olderThan
            LIMIT @batch
        )
        """;

    private const string PurgeInboxSql = """
        DELETE FROM messaging.inbox_messages
        WHERE ctid IN (
            SELECT ctid FROM messaging.inbox_messages
            WHERE received_at < @olderThan
            LIMIT @batch
        )
        """;

    /// <inheritdoc />
    public Task<int> PurgeSentAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeSentSql, new { olderThan, batch = batchSize }, cancellationToken: ct));

    /// <inheritdoc />
    public Task<int> PurgeDeadAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeDeadSql, new { olderThan, batch = batchSize }, cancellationToken: ct));

    /// <inheritdoc />
    public Task<int> PurgeAdmittedAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeInboxSql, new { olderThan, batch = batchSize }, cancellationToken: ct));
}
```

- [ ] **Step 4: Add the DI entry point**

Create `src/neutral/Themia.Messaging.PostgreSql/ServiceCollectionExtensions.cs`:

```csharp
using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>DI entry point for the PostgreSQL messaging dialects.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL claim and purge dialects, resolving the connection string from
    /// <c>ConnectionStrings:<paramref name="connectionStringName"/></c> at first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// Name of the connection string the dialects use. Defaults to <c>"Default"</c>, matching
    /// <c>MessagingModuleOptions.ConnectionStringName</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionStringName"/> is null or whitespace.</exception>
    public static IServiceCollection AddThemiaMessagingPostgreSql(
        this IServiceCollection services, string connectionStringName = "Default")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // Maps snake_case columns onto the PascalCase record parameters of ClaimedMessageRow.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.TryAddSingleton<IOutboxDialect<ClaimedMessageRow>>(sp =>
            new PostgresMessagingDialect(Resolve(sp, connectionStringName)));

        services.TryAddSingleton<PostgresMessagingPurgeDialect>();
        services.TryAddSingleton<IOutboxPurgeDialect<ClaimedMessageRow>>(
            sp => sp.GetRequiredService<PostgresMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxPurgeDialect>(
            sp => sp.GetRequiredService<PostgresMessagingPurgeDialect>());

        return services;
    }

    private static string Resolve(IServiceProvider sp, string name)
        => sp.GetRequiredService<IConfiguration>().GetConnectionString(name)
           ?? throw new InvalidOperationException($"Connection string '{name}' was not found.");
}
```

- [ ] **Step 5: Write the integration test**

Create `tests/Themia.Modules.Messaging.IntegrationTests/` by copying the project file from `tests/Themia.Modules.Notifications.IntegrationTests/` and repointing its `ProjectReference`s at `Themia.Modules.Messaging` and `Themia.Messaging.PostgreSql`.

Create `OutboxRoundTripTests.cs` modelled on `tests/Themia.Modules.Notifications.IntegrationTests/OutboxRoundTripTests.cs` — read that file first, it is the template. Cover:

1. `Drain_delivers_a_pending_message_and_marks_it_sent`
2. `Failing_dispatcher_retries_then_dead_letters_after_max_attempts`
3. `Permanent_failure_dead_letters_immediately_without_retry`
4. `Purge_deletes_sent_rows_past_the_window_and_leaves_recent_ones`
5. `Purge_deletes_in_batches_and_terminates` — insert `PurgeBatchSize + 5` sent rows past the window, run one drain cycle, assert the table is empty (proving the loop repeats rather than deleting one batch)
6. `Unique_constraint_rejects_the_same_message_id_for_the_same_destination`
7. `Same_message_id_is_allowed_for_two_different_destinations`

- [ ] **Step 6: Run the integration tests**

Run: `dotnet test tests/Themia.Modules.Messaging.IntegrationTests/Themia.Modules.Messaging.IntegrationTests.csproj`
Expected: PASS. Requires Docker; verify with `docker info` first.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Messaging.PostgreSql tests/Themia.Modules.Messaging.IntegrationTests Themia.sln
git commit -m "feat(messaging): add PostgreSQL claim and purge dialects"
```

---

### Task 5: Inbox admission on the Dapper peer

**Files:**
- Create: `src/neutral/Themia.Messaging.PostgreSql/PostgresInboxAdmission.cs`
- Create: `src/modules/Themia.Modules.Messaging/Inbox/DapperInboxStore.cs`
- Create: `src/modules/Themia.Modules.Messaging/Inbox/InboxPurgeService.cs`
- Modify: `src/modules/Themia.Modules.Messaging/DependencyInjection/MessagingServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Messaging.IntegrationTests/InboxAdmissionTests.cs`

**Interfaces:**
- Consumes: `IInboxStore.TryAdmitAsync(string origin, Guid messageId, string type, CancellationToken)`, `InboxAdmission`, `IDapperConnectionContext`, `IInboxPurgeDialect`, `MessagingModuleOptions`.
- Produces: `IInboxAdmissionDialect` with `TryAdmitAsync(DbConnection, DbTransaction?, string origin, Guid messageId, string? tenantId, string type, CancellationToken) -> Task<bool>`; `DapperInboxStore : IInboxStore`; `AddThemiaMessagingInbox(IServiceCollection)`.

- [ ] **Step 1: Add the admission dialect contract**

Create `src/neutral/Themia.Messaging/Inbox/IInboxAdmissionDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Messaging.Inbox;

/// <summary>
/// Engine-specific insert-if-not-exists for inbox admission. Takes the caller's connection and
/// transaction rather than opening its own: admission must commit with the application's state change,
/// or a crash between the two loses the message permanently.
/// </summary>
public interface IInboxAdmissionDialect
{
    /// <summary>
    /// Attempts to record the message as admitted, in ONE statement. A read-then-write would let two
    /// concurrent deliveries of the same message both observe "not seen" and both process it.
    /// </summary>
    /// <param name="connection">The caller's open connection.</param>
    /// <param name="transaction">The caller's ambient transaction, if any.</param>
    /// <param name="origin">The system that originated the message.</param>
    /// <param name="messageId">The sender's stable message identifier.</param>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/>.</param>
    /// <param name="type">The logical message type, recorded for diagnostics.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when this call inserted the record; <see langword="false"/> when it already existed.</returns>
    Task<bool> TryAdmitAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string origin,
        Guid messageId,
        string? tenantId,
        string type,
        CancellationToken ct);
}
```

- [ ] **Step 2: Implement it for PostgreSQL**

Create `src/neutral/Themia.Messaging.PostgreSql/PostgresInboxAdmission.cs`:

```csharp
using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>PostgreSQL inbox admission. <c>ON CONFLICT DO NOTHING</c> makes the check-and-insert a single
/// atomic statement, and <c>received_at</c> is left to the database clock so a skewed app-server clock
/// cannot distort the retention window (the same reasoning as coord #0026's DB-generated sentAt).</summary>
internal sealed class PostgresInboxAdmission : IInboxAdmissionDialect
{
    private const string AdmitSql = """
        INSERT INTO messaging.inbox_messages (origin, message_id, tenant_id, type, received_at)
        VALUES (@origin, @messageId, @tenantId, @type, now())
        ON CONFLICT (origin, message_id) DO NOTHING
        """;

    /// <inheritdoc />
    public async Task<bool> TryAdmitAsync(
        DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
        string? tenantId, string type, CancellationToken ct)
    {
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            AdmitSql,
            new { origin, messageId, tenantId, type },
            transaction,
            cancellationToken: ct));

        return inserted == 1;
    }
}
```

Register it in `ServiceCollectionExtensions.AddThemiaMessagingPostgreSql`, next to the other dialects:

```csharp
        services.TryAddSingleton<IInboxAdmissionDialect, PostgresInboxAdmission>();
```

- [ ] **Step 3: Implement the store over the caller's transaction**

Create `src/modules/Themia.Modules.Messaging/Inbox/DapperInboxStore.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Messaging.Inbox;

namespace Themia.Modules.Messaging.Inbox;

/// <summary>
/// Dapper-peer <see cref="IInboxStore"/>. Runs on the caller's ambient connection and transaction so the
/// admission record and the application's state change commit together — the whole point of the inbox.
/// </summary>
/// <remarks>
/// This is the sanctioned data-layer raw-connection path. There is deliberately no EF implementation:
/// <c>Themia.Framework.Data.EFCore</c> exposes no connection or transaction access, and a version that
/// opened its own connection would reintroduce the loss window it exists to close.
/// </remarks>
internal sealed class DapperInboxStore(
    IDapperConnectionContext connectionContext,
    IInboxAdmissionDialect dialect,
    ITenantContext tenantContext) : IInboxStore
{
    /// <inheritdoc />
    public async Task<InboxAdmission> TryAdmitAsync(
        string origin, Guid messageId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var connection = await connectionContext.GetOpenConnectionAsync(ct).ConfigureAwait(false);

        var inserted = await dialect.TryAdmitAsync(
            connection,
            connectionContext.CurrentTransaction,
            origin,
            messageId,
            tenantContext.Current?.Value,
            type,
            ct).ConfigureAwait(false);

        return inserted ? InboxAdmission.Accepted : InboxAdmission.Duplicate;
    }
}
```

**Before writing this file**, open `src/framework/Themia.Framework.Core/Abstractions/Tenancy/ITenantContext.cs` and confirm how the current tenant is exposed. If the member is not `Current?.Value`, use whatever it actually is and adjust the argument accordingly.

- [ ] **Step 4: Add the inbox purge service and registration**

Create `src/modules/Themia.Modules.Messaging/Inbox/InboxPurgeService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Modules.Messaging.Inbox;

/// <summary>
/// Deletes expired inbox admission records on a slow cadence. A background service rather than a
/// scheduled job so the module does not force a scheduler dependency on every adopter.
/// </summary>
internal sealed class InboxPurgeService(
    IInboxPurgeDialect purgeDialect,
    IOutboxDialect<ClaimedMessageRow> connectionSource,
    MessagingModuleOptions options,
    TimeProvider time,
    ILogger<InboxPurgeService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.PurgeEnabled)
                {
                    await PurgeAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — clean shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox purge cycle failed; retrying on the next interval.");
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.InboxRetentionDays);

        await using var connection = connectionSource.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var total = 0;
        int deleted;
        do
        {
            ct.ThrowIfCancellationRequested();
            deleted = await purgeDialect.PurgeAdmittedAsync(connection, cutoff, 1000, ct).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == 1000);

        if (total > 0)
        {
            logger.LogInformation("Inbox purge removed {Deleted} admission records.", total);
        }
    }
}
```

Add to `MessagingServiceCollectionExtensions`:

```csharp
    /// <summary>
    /// Adds the deduplicating inbox. REQUIRES the Dapper data peer: admission must commit inside the
    /// caller's transaction, and only the Dapper peer exposes an ambient connection. Throws at startup on
    /// an EF-only host rather than degrading to a non-transactional admission that could lose messages.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddThemiaMessagingInbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(d => d.ServiceType != typeof(IDapperConnectionContext)))
        {
            throw new InvalidOperationException(
                "AddThemiaMessagingInbox requires the Dapper data peer: register AddThemiaDapper{Postgres|MySql|SqlServer}(...) first. "
                + "The inbox is not supported on the EF peer because admission must commit inside the caller's transaction, "
                + "and Themia.Framework.Data.EFCore exposes no ambient connection.");
        }

        services.TryAddScoped<IInboxStore, DapperInboxStore>();
        services.AddHostedService<InboxPurgeService>();

        return services;
    }
```

Add `using Themia.Framework.Data.Dapper.Connection;`, `using Themia.Messaging.Inbox;`, `using Themia.Modules.Messaging.Inbox;` and `using System.Linq;` to that file.

- [ ] **Step 5: Write the integration tests**

Create `tests/Themia.Modules.Messaging.IntegrationTests/InboxAdmissionTests.cs` covering:

1. `First_admission_is_accepted`
2. `Second_admission_of_the_same_message_is_a_duplicate`
3. `Same_message_id_from_a_different_origin_is_accepted` — proves the key is `(origin, message_id)`
4. `Concurrent_admissions_of_the_same_message_admit_exactly_once` — run 8 concurrent `TryAdmitAsync` calls on separate scopes; assert exactly one `Accepted`
5. `Rolled_back_admission_can_be_admitted_again` — admit inside a transaction, roll back, admit again, assert `Accepted`. **This is the load-bearing test**: if it returns `Duplicate`, admission is not joining the caller's transaction and a crash between admit and apply would lose the message
6. `Received_at_is_set_by_the_database` — admit, read the row, assert `received_at` is within a minute of now
7. `AddThemiaMessagingInbox_throws_when_only_the_EF_peer_is_registered`

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Themia.Modules.Messaging.IntegrationTests/Themia.Modules.Messaging.IntegrationTests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Messaging src/neutral/Themia.Messaging.PostgreSql src/modules/Themia.Modules.Messaging tests/Themia.Modules.Messaging.IntegrationTests
git commit -m "feat(messaging): add transactional inbox admission on the Dapper peer"
```

---

### Task 6: Notifications purge retrofit

**Files:**
- Create: `src/modules/Themia.Modules.Notifications/Migrations/NotificationsPurgeIndexMigration.cs`
- Create: `src/modules/Themia.Modules.Notifications.PostgreSql/PostgresNotificationsPurgeDialect.cs`
- Modify: `src/modules/Themia.Modules.Notifications/Migrations/NotificationsSchemaMigration.cs:52`
- Modify: `src/modules/Themia.Modules.Notifications/NotificationsModuleOptions.cs`
- Modify: `src/modules/Themia.Modules.Notifications/DependencyInjection/NotificationsServiceCollectionExtensions.cs`
- Modify: `src/modules/Themia.Modules.Notifications.PostgreSql/ServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/DependencyInjection/AddThemiaNotificationsModuleTests.cs`

**Interfaces:**
- Consumes: `IOutboxPurgeDialect<ClaimedOutboxRow>`, `OutboxDrainerOptions<ClaimedOutboxRow>`.
- Produces: `NotificationsModuleOptions` gains `PurgeEnabled` (default **false**), `SentRetentionDays` (7), `DeadRetentionDays` (90); `PostgresNotificationsPurgeDialect`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Themia.Modules.Notifications.Tests/DependencyInjection/AddThemiaNotificationsModuleTests.cs`:

```csharp
    // Purge must be OFF unless the adopter asks for it: enabling retention on an existing deployment
    // deletes historical sent rows on the first run, which must never arrive via a version bump.
    [Fact]
    public void AddThemiaNotificationsModule_ShouldDefault_PurgeDisabled()
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxDrainerOptions<ClaimedOutboxRow>>();

        Assert.False(options.PurgeEnabled);
    }

    [Fact]
    public void AddThemiaNotificationsModule_ShouldPropagate_PurgeSettings_WhenEnabled()
    {
        var services = new ServiceCollection();
        services.AddThemiaNotificationsModule(o =>
        {
            o.ConnectionStringName = "X";
            o.PurgeEnabled = true;
            o.SentRetentionDays = 3;
            o.DeadRetentionDays = 45;
        });
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxDrainerOptions<ClaimedOutboxRow>>();

        Assert.True(options.PurgeEnabled);
        Assert.Equal(3, options.SentRetentionDays);
        Assert.Equal(45, options.DeadRetentionDays);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Modules.Notifications.Tests/Themia.Modules.Notifications.Tests.csproj`
Expected: FAIL — `NotificationsModuleOptions` has no `PurgeEnabled`.

- [ ] **Step 3: Add the options**

Append to `NotificationsModuleOptions.cs`, inside the class:

```csharp
    /// <summary>
    /// Whether the drainer also purges terminal outbox rows. Defaults to <see langword="false"/>: this
    /// module has shipped without a purge since 0.6.x, so enabling it by default would silently delete
    /// every historical sent row on the first run after an upgrade. Opt in deliberately.
    /// </summary>
    public bool PurgeEnabled { get; set; }

    /// <summary>How long delivered rows are kept once <see cref="PurgeEnabled"/> is set. Default 7 days.</summary>
    public int SentRetentionDays { get; set; } = 7;

    /// <summary>How long dead-lettered rows are kept once <see cref="PurgeEnabled"/> is set. Default 90 days.</summary>
    public int DeadRetentionDays { get; set; } = 90;
```

Append to `Validate()`:

```csharp
        if (SentRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(SentRetentionDays), SentRetentionDays, "Must be at least 1 day.");
        if (DeadRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(DeadRetentionDays), DeadRetentionDays, "Must be at least 1 day.");
```

- [ ] **Step 4: Propagate them into the drainer options**

In `NotificationsServiceCollectionExtensions.cs`, extend the existing `OutboxDrainerOptions<ClaimedOutboxRow>` registration:

```csharp
        services.TryAddSingleton(new OutboxDrainerOptions<ClaimedOutboxRow>
        {
            DrainIntervalSeconds = options.DrainIntervalSeconds,
            MaxBatchSize = options.MaxBatchSize,
            MaxAttempts = options.MaxAttempts,
            LeaseSeconds = options.LeaseSeconds,
            PurgeEnabled = options.PurgeEnabled,
            SentRetentionDays = options.SentRetentionDays,
            DeadRetentionDays = options.DeadRetentionDays,
        });
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Modules.Notifications.Tests/Themia.Modules.Notifications.Tests.csproj`
Expected: PASS — 50 tests.

- [ ] **Step 6: Add the purge index migration**

Create `src/modules/Themia.Modules.Notifications/Migrations/NotificationsPurgeIndexMigration.cs`:

```csharp
using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

/// <summary>Adds the composite index the retention purge scans. A NEW migration rather than an edit to
/// <see cref="NotificationsSchemaMigration"/>, which is already deployed — migrations are forward-only.</summary>
[Migration(202607310002, "Themia.Notifications: add outbox purge index")]
public sealed class NotificationsPurgeIndexMigration : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON \"notifications\".\"outbox_messages\" (status, sent_at);");
        IfDatabase("sqlserver").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON [notifications].[outbox_messages] (status, sent_at);");
        IfDatabase("mysql").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON outbox_messages (status, sent_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Execute.Sql("DROP INDEX \"notifications\".ix_outbox_purge;");
        IfDatabase("sqlserver").Execute.Sql("DROP INDEX ix_outbox_purge ON [notifications].[outbox_messages];");
        IfDatabase("mysql").Execute.Sql("DROP INDEX ix_outbox_purge ON outbox_messages;");
    }
}
```

- [ ] **Step 7: Correct the misleading comment**

In `src/modules/Themia.Modules.Notifications/Migrations/NotificationsSchemaMigration.cs`, replace line 52:

```csharp
        // Operational outbox row — not soft-deletable (purged, not tombstoned).
```

with:

```csharp
        // Operational outbox row — not soft-deletable. Terminal rows are deleted by the retention purge,
        // which is OPT-IN via NotificationsModuleOptions.PurgeEnabled; without it this table grows forever.
```

- [ ] **Step 8: Implement the PostgreSQL purge dialect**

Create `src/modules/Themia.Modules.Notifications.PostgreSql/PostgresNotificationsPurgeDialect.cs`:

```csharp
using System.Data.Common;

using Dapper;

using Themia.Messaging.Outbox;
using Themia.Modules.Notifications.Outbox;

namespace Themia.Modules.Notifications.PostgreSql;

/// <summary>PostgreSQL retention deletes for the notifications outbox. Bounded by <c>LIMIT</c> via a
/// <c>ctid</c> subquery so no single statement holds a long lock on a large table.</summary>
internal sealed class PostgresNotificationsPurgeDialect : IOutboxPurgeDialect<ClaimedOutboxRow>
{
    private const string PurgeSentSql = """
        DELETE FROM notifications.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM notifications.outbox_messages
            WHERE status = 2 AND sent_at < @olderThan
            LIMIT @batch
        )
        """;

    private const string PurgeDeadSql = """
        DELETE FROM notifications.outbox_messages
        WHERE ctid IN (
            SELECT ctid FROM notifications.outbox_messages
            WHERE status = 4 AND next_attempt_at < @olderThan
            LIMIT @batch
        )
        """;

    /// <inheritdoc />
    public Task<int> PurgeSentAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeSentSql, new { olderThan, batch = batchSize }, cancellationToken: ct));

    /// <inheritdoc />
    public Task<int> PurgeDeadAsync(DbConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            PurgeDeadSql, new { olderThan, batch = batchSize }, cancellationToken: ct));
}
```

Register it in `src/modules/Themia.Modules.Notifications.PostgreSql/ServiceCollectionExtensions.cs`, inside `AddThemiaNotificationsPostgreSql`:

```csharp
        services.TryAddSingleton<IOutboxPurgeDialect<ClaimedOutboxRow>, PostgresNotificationsPurgeDialect>();
```

Add `using Themia.Messaging.Outbox;` to that file.

- [ ] **Step 9: Add an integration test for the opt-in behaviour**

Append to `tests/Themia.Modules.Notifications.IntegrationTests/OutboxRoundTripTests.cs` a test named `Purge_does_nothing_when_disabled_and_deletes_old_sent_rows_when_enabled`: insert two sent rows (one 30 days old, one 1 hour old), run a drain cycle with `PurgeEnabled = false` and assert both survive, then run one with `PurgeEnabled = true, SentRetentionDays = 7` and assert only the recent row survives.

- [ ] **Step 10: Run the full suite**

```bash
dotnet build Themia.sln --no-incremental
dotnet test tests/Themia.Modules.Notifications.Tests/Themia.Modules.Notifications.Tests.csproj
dotnet test tests/Themia.Modules.Notifications.IntegrationTests/Themia.Modules.Notifications.IntegrationTests.csproj
```
Expected: all PASS.

- [ ] **Step 11: Commit**

```bash
git add src/modules/Themia.Modules.Notifications src/modules/Themia.Modules.Notifications.PostgreSql tests/Themia.Modules.Notifications.Tests tests/Themia.Modules.Notifications.IntegrationTests
git commit -m "feat(notifications): add opt-in retention purge for the outbox"
```

---

### Task 7: MySQL and SQL Server dialects

**Files:**
- Create: `src/neutral/Themia.Messaging.MySql/` — `Themia.Messaging.MySql.csproj`, `MySqlMessagingDialect.cs`, `MySqlMessagingPurgeDialect.cs`, `MySqlInboxAdmission.cs`, `ServiceCollectionExtensions.cs`, PublicAPI files
- Create: `src/neutral/Themia.Messaging.SqlServer/` — the same five files
- Create: `src/modules/Themia.Modules.Notifications.MySql/MySqlNotificationsPurgeDialect.cs`
- Create: `src/modules/Themia.Modules.Notifications.SqlServer/SqlServerNotificationsPurgeDialect.cs`

**Interfaces:**
- Consumes: everything produced by Tasks 1, 4 and 5.
- Produces: `AddThemiaMessagingMySql(...)`, `AddThemiaMessagingSqlServer(...)`, both registering claim, purge and admission dialects.

- [ ] **Step 1: Create both projects**

Mirror `src/neutral/Themia.Messaging.PostgreSql/` exactly, swapping the driver package (`MySqlConnector` / `Microsoft.Data.SqlClient`) and connection type. Read `src/modules/Themia.Modules.Notifications.MySql/MySqlNotificationsDialect.cs` and `...SqlServer/SqlServerNotificationsDialect.cs` first — they already solve the per-engine claim differences and are the template for `ClaimAsync`.

- [ ] **Step 2: Write the engine-specific purge SQL**

MySQL supports `DELETE ... LIMIT` directly:

```csharp
    private const string PurgeSentSql = """
        DELETE FROM outbox_messages
        WHERE status = 2 AND sent_at < @olderThan
        LIMIT @batch
        """;
```

SQL Server uses `DELETE TOP`:

```csharp
    private const string PurgeSentSql = """
        DELETE TOP (@batch) FROM [messaging].[outbox_messages]
        WHERE status = 2 AND sent_at < @olderThan
        """;
```

Write the `PurgeDead` and `PurgeAdmitted` variants the same way, matching the PostgreSQL predicates (`status = 4 AND next_attempt_at < @olderThan`, and `received_at < @olderThan` respectively).

- [ ] **Step 3: Write the engine-specific admission SQL**

MySQL:

```csharp
    private const string AdmitSql = """
        INSERT IGNORE INTO inbox_messages (origin, message_id, tenant_id, type, received_at)
        VALUES (@origin, @messageId, @tenantId, @type, UTC_TIMESTAMP(6))
        """;
```

SQL Server has no insert-or-ignore, and `MERGE` is deliberately avoided (not race-free without `HOLDLOCK`, with a long history of concurrency defects). Use a guarded insert and treat the unique-violation race as a duplicate:

```csharp
    private const string AdmitSql = """
        INSERT INTO [messaging].[inbox_messages] (origin, message_id, tenant_id, type, received_at)
        SELECT @origin, @messageId, @tenantId, @type, SYSDATETIMEOFFSET()
        WHERE NOT EXISTS (
            SELECT 1 FROM [messaging].[inbox_messages] WITH (UPDLOCK, HOLDLOCK)
            WHERE origin = @origin AND message_id = @messageId
        )
        """;
```

```csharp
    /// <inheritdoc />
    public async Task<bool> TryAdmitAsync(
        DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
        string? tenantId, string type, CancellationToken ct)
    {
        try
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                AdmitSql, new { origin, messageId, tenantId, type }, transaction, cancellationToken: ct));
            return inserted == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Lost the insert race — another delivery of this message admitted first. That is a duplicate,
            // not an error.
            return false;
        }
    }
```

- [ ] **Step 4: Add the Notifications purge dialects for both engines**

Copy `PostgresNotificationsPurgeDialect.cs` into each package, swapping the batching syntax per Step 2 and the table reference (`outbox_messages` for MySQL, `[notifications].[outbox_messages]` for SQL Server). Register each in that package's `ServiceCollectionExtensions`.

- [ ] **Step 5: Run the integration suites against all three engines**

Parameterise the messaging integration tests over the three Testcontainers images the Notifications integration tests already use — read `tests/Themia.Modules.Notifications.IntegrationTests/` for the existing per-engine fixtures and mirror them.

Run: `dotnet test tests/Themia.Modules.Messaging.IntegrationTests/Themia.Modules.Messaging.IntegrationTests.csproj`
Expected: PASS on all three engines.

- [ ] **Step 6: Full clean build and whole-solution test**

```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln
```
Expected: `Build succeeded.` and all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Messaging.MySql src/neutral/Themia.Messaging.SqlServer src/modules/Themia.Modules.Notifications.MySql src/modules/Themia.Modules.Notifications.SqlServer tests Themia.sln
git commit -m "feat(messaging): add MySQL and SQL Server dialects for outbox, inbox and purge"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| `messaging.outbox_messages` table + indexes | 2 |
| `messaging.inbox_messages` table, `PK (origin, message_id)` | 2 |
| DB-generated `received_at` | 5 (PostgreSQL), 7 (MySQL, SQL Server) |
| Admission joins caller's transaction | 5 |
| Admission Dapper-only, fails fast on EF | 5 |
| Admit-before-apply documented on the interface | done in `7b1d00b` |
| `IOutboxPurgeDialect` / `IInboxPurgeDialect` split | 1 |
| Purge from the drain loop, not a scheduler | 1 |
| Batched deletes | 1 (loop), 4 and 7 (per-engine SQL) |
| Windows 7d / 90d / 90d, configurable | 1, 2 |
| Notifications retrofit: 3 dialects, new index migration, comment fix | 6, 7 |
| Purge on for messaging, off for Notifications | 2 (default `true`), 6 (default `false`) |
| No `MERGE` on SQL Server | 7 |
| net10-only | 2, 4, 7 |
| Repository-backed outbox store, peer-agnostic | 3 |

**Deviation from the spec, recorded deliberately:** the spec's table sets `InboxRetentionDays` default to 30d against a 90d dead-letter window. Task 2 raises the default to **90d** and adds a `Validate()` rule rejecting `InboxRetentionDays < DeadRetentionDays`. A 30/90 pair guarantees the failure the spec warns about — an admission record forgotten while the sender is still retrying. The spec's own reasoning ("must exceed any redelivery age the outbox can produce") requires this; the number in its table contradicted it.

**Type consistency:** `ClaimedMessageRow` positional parameters (`Id`, `MessageId`, `TenantId`, `Type`, `Payload`, `Destination`, `Origin`, `EntityKey`, `Version`, `Attempts`) match the `RETURNING` column order in Task 4. `OutboxDrainerOptions<TRow>` property names are identical across Tasks 1, 3 and 6. `IInboxStore.TryAdmitAsync(string, Guid, string, CancellationToken)` in Task 5 matches the committed contract.

**Known gap requiring judgment during execution:** Task 3 Step 5 and Task 5 Step 3 tell the engineer to read `EntityConfiguration/` and `ITenantContext` rather than showing code, because the exact shapes are not established in this session. Both are marked with what to look for.
