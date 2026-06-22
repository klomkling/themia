# Themia.Modules.Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the tenant-aware Notifications module — a transactional outbox, a near-real-time background drainer (per-engine atomic claim + lease + backoff), an `INotificationDispatcher` routing events to channels via per-tenant/user preferences, an in-app notification store, and per-tenant provider config — over the already-shipped `Themia.Notifications` neutral core.

**Architecture:** New net10 package `Themia.Modules.Notifications`, an `IThemiaModule` (`ThemiaModuleBase`) that owns its FluentMigrator schema across PostgreSQL / MySQL / SQL Server. Enqueue is transactional in the caller's `IUnitOfWork` (EF `DbContext` or Dapper UoW peer). Drain runs independently in a hosted `BackgroundService` that claims rows atomically via an `INotificationsSqlDialect` (engine-specific skip-locked SQL), dispatches by channel to the neutral senders, and applies exponential backoff → `dead`. In-app is written directly (no outbox hop).

**Tech Stack:** .NET 10, EF Core + Dapper (selectable peers), FluentMigrator (DDL authority), `Microsoft.Extensions.Hosting` (`BackgroundService`), Handlebars.Net (via the neutral core's `INotificationTemplateRenderer`), xUnit + Testcontainers (postgres/mssql/mysql).

**Authoritative spec:** `docs/superpowers/specs/2026-06-22-themia-notifications-design.md`. Where this plan and the spec disagree, the spec wins — but this plan resolves the spec's open implementation choices (rendering happens at enqueue; outbox rows are operational, not soft-deletable; provider-config secrets are plain columns in v1 with Data Protection deferred).

**Ponytail simplifications (deliberate, marked in code with `// ponytail:` comments):**
- Outbox/in-app bodies are **rendered at enqueue time** (drainer only sends a self-contained row — no template state in the drainer).
- `OutboxMessage` is **not** soft-deletable (operational rows are purged, not tombstoned); only `ITenantEntity` + explicit operational columns.
- `TenantProviderConfig` stores SMTP/SMS secrets as **plain columns** in v1; encryption-at-rest (Data Protection) is a flagged follow-on, not built here.
- No new dependency added beyond what Storage already uses; the drainer is a single hosted service, not a worker pool.

---

## File Structure

```
src/modules/Themia.Modules.Notifications/
  Themia.Modules.Notifications.csproj
  NotificationsModule.cs                       # IThemiaModule; owns migration run
  NotificationsModuleOptions.cs                # ConnectionStringName, DrainIntervalSeconds, MaxBatchSize, MaxAttempts, LeaseSeconds + Validate()
  Entities/
    OutboxMessage.cs                           # Entity<Guid>, ITenantEntity
    OutboxStatus.cs                            # enum pending|sending|sent|failed|dead
    InAppNotification.cs                       # AuditableEntity<Guid>, ITenantEntity
    NotificationPreference.cs                  # AuditableEntity<Guid>, ITenantEntity
    TenantProviderConfig.cs                    # AuditableEntity<Guid>, ITenantEntity
  Migrations/
    NotificationsSchemaMigration.cs            # IfDatabase × postgresql|mysql|sqlserver
  Outbox/
    IOutboxStore.cs                            # EnqueueAsync (transactional, caller's UoW)
    EfOutboxStore.cs                           # internal — EF peer
    DapperOutboxStore.cs                       # internal — Dapper peer
    INotificationsSqlDialect.cs                # per-engine claim/complete/fail/reclaim SQL + CreateConnection
    PostgresNotificationsDialect.cs            # FOR UPDATE SKIP LOCKED
    MySqlNotificationsDialect.cs               # FOR UPDATE SKIP LOCKED
    SqlServerNotificationsDialect.cs           # UPDATE TOP ... WITH (READPAST,UPDLOCK,ROWLOCK) OUTPUT
    ClaimedOutboxRow.cs                        # row DTO returned by the claim
    BackoffPolicy.cs                           # pure: next attempt time + dead decision
    DrainSignal.cs                             # in-process wake channel
    OutboxDrainer.cs                           # internal — BackgroundService
  Dispatch/
    INotificationDispatcher.cs                 # DispatchAsync(NotificationRequest, ct)
    NotificationRequest.cs                     # what the app hands in (event → recipients/channels)
    NotificationDispatcher.cs                  # internal — routes to outbox / in-app
    IPreferenceResolver.cs                     # resolves enabled channels (+ locale) per tenant/user
    PreferenceResolver.cs                      # internal
  Config/
    IProviderConfigResolver.cs                 # per-tenant SMTP/SMS + global fallback
    ProviderConfigResolver.cs                  # internal
  Stores/
    IInAppNotificationStore.cs                 # write + query in-app
    EfInAppNotificationStore.cs / DapperInAppNotificationStore.cs
    INotificationPreferenceStore.cs
    EfNotificationPreferenceStore.cs / DapperNotificationPreferenceStore.cs
    ITenantProviderConfigStore.cs
    EfTenantProviderConfigStore.cs / DapperTenantProviderConfigStore.cs
  EntityConfiguration/                         # EF IEntityTypeConfiguration<T> for the 4 entities
    OutboxMessageConfiguration.cs … (4 files)
    ThemiaNotificationsModelBuilderExtensions.cs   # ApplyThemiaNotifications(modelBuilder)
  Mapping/
    NotificationsDapperMappings.cs             # Apply(EntityMappingRegistry)
  DependencyInjection/
    NotificationsServiceCollectionExtensions.cs    # AddThemiaNotificationsModule + NotificationsBuilder
  PublicAPI.Shipped.txt
  PublicAPI.Unshipped.txt

tests/Themia.Modules.Notifications.Tests/                 # unit (no DB/network)
tests/Themia.Modules.Notifications.IntegrationTests/      # Testcontainers × 3 engines
```

**Decomposition rationale:** Phase A (Tasks 1–6) builds persistence (project, entities, schema, EF + Dapper mappings/stores) — testable on its own via the migration integration test. Phase B (Tasks 7–11) builds the outbox + drainer — the correctness core. Phase C (Tasks 12–16) builds dispatch/preferences/in-app/config + module wiring + release. Each phase produces working, tested software.

---

## Conventions every task must follow

- `System.Text.Json` only — never `Newtonsoft.Json`. `ILogger<T>` only — never `Console.*`.
- Parameterized SQL only. No string-concatenated values into SQL; identifiers/limits are the only interpolation in dialect templates.
- THEMIA101: no log-and-rethrow. The drainer logs a send failure **once** (it owns the outcome); request-path code lets exceptions propagate to `ProblemDetailsMiddleware`.
- Every public type/member gets XML docs (clean under `TreatWarningsAsErrors`; `RS0016` on undocumented public API). Add new public API lines to `PublicAPI.Unshipped.txt` in the same task that introduces them.
- Never log credentials or full recipient PII (mask via the core's `RecipientRedaction` where a recipient must appear in a log).
- **Entity Id assignment:** `Entity<TId>.Id` has a `protected set` (framework convention). Each entity exposes `public void SetId(Guid id) => Id = id;` (added in Task 2). NEVER write `Id = Guid.NewGuid()` in an object initializer — construct the entity, then call `entity.SetId(Guid.NewGuid())`. (`TenantId`, `CreatedAt`, and the operational columns are normal public setters.)
- **Data-access seam (UPDATED after Task 6 — supersedes the EF/Dapper store-pair design below):** The peer-agnostic seam is the framework repository abstraction in `Themia.Framework.Data.Abstractions` — `IRepository<T, Guid>` (writes: `AddAsync`, `Update`), `IReadRepository<T, Guid>` (reads: `ListAsync(spec)`, `FirstOrDefaultAsync(spec)`), `IUnitOfWork` (`SaveChangesAsync`), and `ISpecification<T>`. A SINGLE store class per concern injects these; the framework binds them to EF or Dapper based on which the adopter registered. **Do NOT write `EfXStore` + `DapperXStore` pairs** (they would be identical) and **do NOT inject `ThemiaDbContext` / `DbContext` directly** — `EfRepository.AddAsync` stamps `ITenantEntity.TenantId`, so `context.Set<T>().Add` + `SaveChanges` leaves `tenant_id = NULL` and BREAKS tenant isolation. Reads filter by tenant automatically through the repository. There is NO public raw pending-sink (`IPendingOperationSink`/`DapperUnitOfWork` are internal); stage writes via `IRepository.AddAsync` and flush via `IUnitOfWork.SaveChangesAsync`. (`TenantStorage` is the reference: it injects `IRepository<StorageObject, Guid>` + `IUnitOfWork`.) The ONLY raw-connection path in this module is the drainer's per-engine claim dialect (Task 9), which opens its OWN connection (`new NpgsqlConnection(...)` etc.) — that is analyzer-clean (THEMIA103 only flags `IDapperConnectionContext.GetOpenConnectionAsync`, not fresh connections) and correct, because the drainer is cross-tenant infra.
- TDD: write the failing test, run it red, implement minimally, run it green, commit. Commit messages imperative, no co-author trailers.
- Run `dotnet build Themia.sln --no-incremental` before finishing a task that adds public API, to surface `RS0016`.

---

# Phase A — Persistence

### Task 1: Scaffold the module project

**Files:**
- Create: `src/modules/Themia.Modules.Notifications/Themia.Modules.Notifications.csproj`
- Create: `src/modules/Themia.Modules.Notifications/NotificationsModuleOptions.cs`
- Create: `src/modules/Themia.Modules.Notifications/PublicAPI.Shipped.txt` (empty)
- Create: `src/modules/Themia.Modules.Notifications/PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Modules.Notifications.Tests/Themia.Modules.Notifications.Tests.csproj`
- Modify: `Themia.sln` (add both projects)

- [ ] **Step 1: Write the failing test** — `tests/Themia.Modules.Notifications.Tests/NotificationsModuleOptionsTests.cs`

```csharp
using Themia.Modules.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests;

public class NotificationsModuleOptionsTests
{
    [Fact]
    public void Validate_ShouldThrow_WhenConnectionStringNameBlank()
    {
        var options = new NotificationsModuleOptions { ConnectionStringName = " " };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldThrow_WhenDrainIntervalNotPositive(int seconds)
    {
        var options = new NotificationsModuleOptions { DrainIntervalSeconds = seconds };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenMaxBatchSizeNotPositive()
    {
        var options = new NotificationsModuleOptions { MaxBatchSize = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_ShouldPass_WithDefaults()
    {
        var options = new NotificationsModuleOptions();
        options.Validate(); // does not throw
    }
}
```

- [ ] **Step 2: Create the csproj** — mirror Storage's structure; reference the neutral core, drop S3.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Notifications</PackageId>
    <Description>Tenant-aware notifications — transactional outbox, near-real-time background drainer (per-engine atomic claim + lease + backoff), multi-channel dispatcher with per-tenant/user preferences, in-app store, and per-tenant provider config over EF or Dapper. FluentMigrator schema (PostgreSQL + MySQL + SQL Server).</Description>
    <PackageTags>themia;notifications;outbox;multi-tenancy;email;sms;efcore;dapper</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../neutral/Themia.Notifications/Themia.Notifications.csproj" />
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
    <InternalsVisibleTo Include="Themia.Modules.Notifications.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Notifications.IntegrationTests" />
  </ItemGroup>
</Project>
```

> Note: `Microsoft.AspNetCore.App` framework reference brings in `Microsoft.Extensions.Hosting.Abstractions` (`BackgroundService`, `IHostedService`). If a build error shows `BackgroundService` unresolved, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` is already present — it is sufficient; do **not** add a standalone `Microsoft.Extensions.Hosting` PackageReference.

- [ ] **Step 3: Create `NotificationsModuleOptions.cs`**

```csharp
namespace Themia.Modules.Notifications;

/// <summary>Configuration for the Themia Notifications module.</summary>
public sealed class NotificationsModuleOptions
{
    /// <summary>Name of the connection string (in <c>ConnectionStrings</c>) the module migrates and drains.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>How often the background drainer polls when no in-process signal arrives. Default 5s.</summary>
    public int DrainIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum outbox rows claimed per drain cycle. Default 50.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Attempts before a message is marked <c>dead</c>. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed (sending) row's lease is held before it is reclaimable. Default 120s.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Validates the options, throwing if any value is out of range.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
        if (DrainIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(DrainIntervalSeconds), DrainIntervalSeconds, "Must be at least 1 second.");
        if (MaxBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), MaxBatchSize, "Must be at least 1.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Must be at least 1.");
        if (LeaseSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(LeaseSeconds), LeaseSeconds, "Must be at least 1 second.");
    }
}
```

- [ ] **Step 4: Create the test csproj** — copy `tests/Themia.Modules.Storage.Tests/Themia.Modules.Storage.Tests.csproj`, change the project name and the single `ProjectReference` to point at `../../src/modules/Themia.Modules.Notifications/Themia.Modules.Notifications.csproj`. Keep the same `xunit` / `Microsoft.NET.Test.Sdk` / `coverlet` package references.

- [ ] **Step 5: Add both projects to `Themia.sln`**

Run: `cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia && dotnet sln Themia.sln add src/modules/Themia.Modules.Notifications/Themia.Modules.Notifications.csproj tests/Themia.Modules.Notifications.Tests/Themia.Modules.Notifications.Tests.csproj`

- [ ] **Step 6: Seed `PublicAPI.Unshipped.txt`** with the options type (Shipped.txt stays empty):

```
Themia.Modules.Notifications.NotificationsModuleOptions
Themia.Modules.Notifications.NotificationsModuleOptions.NotificationsModuleOptions() -> void
Themia.Modules.Notifications.NotificationsModuleOptions.ConnectionStringName.get -> string!
Themia.Modules.Notifications.NotificationsModuleOptions.ConnectionStringName.set -> void
Themia.Modules.Notifications.NotificationsModuleOptions.DrainIntervalSeconds.get -> int
Themia.Modules.Notifications.NotificationsModuleOptions.DrainIntervalSeconds.set -> void
Themia.Modules.Notifications.NotificationsModuleOptions.MaxBatchSize.get -> int
Themia.Modules.Notifications.NotificationsModuleOptions.MaxBatchSize.set -> void
Themia.Modules.Notifications.NotificationsModuleOptions.MaxAttempts.get -> int
Themia.Modules.Notifications.NotificationsModuleOptions.MaxAttempts.set -> void
Themia.Modules.Notifications.NotificationsModuleOptions.LeaseSeconds.get -> int
Themia.Modules.Notifications.NotificationsModuleOptions.LeaseSeconds.set -> void
Themia.Modules.Notifications.NotificationsModuleOptions.Validate() -> void
```

> If a clean build reports more required `PublicAPI` lines than shown (e.g. analyzer-formatted nullability), run `dotnet build --no-incremental`, read the `RS0016` messages, and add exactly the lines it names. This applies to every later task that adds public API — the analyzer is the source of truth for the exact text.

- [ ] **Step 7: Run the test** — Run: `dotnet test tests/Themia.Modules.Notifications.Tests --filter NotificationsModuleOptionsTests` — Expected: PASS (4 tests).

- [ ] **Step 8: Commit**

```bash
git add src/modules/Themia.Modules.Notifications tests/Themia.Modules.Notifications.Tests Themia.sln
git commit -m "feat: scaffold Themia.Modules.Notifications project and options"
```

---

### Task 2: Entities

**Files:**
- Create: `Entities/OutboxStatus.cs`, `Entities/OutboxMessage.cs`, `Entities/InAppNotification.cs`, `Entities/NotificationPreference.cs`, `Entities/TenantProviderConfig.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Entities/OutboxMessageTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Themia.Modules.Notifications.Entities;
using Themia.Notifications;            // NotificationChannel (neutral core)
using Xunit;

namespace Themia.Modules.Notifications.Tests.Entities;

public class OutboxMessageTests
{
    [Fact]
    public void New_pending_message_has_zero_attempts_and_pending_status()
    {
        var msg = new OutboxMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "to@example.com",
            Subject = "hi",
            Body = "<p>hi</p>",
            CreatedAt = DateTimeOffset.UnixEpoch,
            NextAttemptAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(OutboxStatus.Pending, msg.Status);
        Assert.Equal(0, msg.Attempts);
        Assert.Null(msg.SentAt);
        Assert.Null(msg.LeaseOwner);
    }
}
```

- [ ] **Step 2: Run it** — Run: `dotnet test tests/Themia.Modules.Notifications.Tests --filter OutboxMessageTests` — Expected: FAIL (types not defined).

- [ ] **Step 3: Create `OutboxStatus.cs`**

```csharp
namespace Themia.Modules.Notifications.Entities;

/// <summary>Lifecycle state of an outbox message.</summary>
public enum OutboxStatus
{
    /// <summary>Awaiting its first (or a retried) send.</summary>
    Pending = 0,
    /// <summary>Claimed by a drainer and in flight.</summary>
    Sending = 1,
    /// <summary>Delivered to the provider successfully.</summary>
    Sent = 2,
    /// <summary>Last attempt failed; eligible for retry until the attempt cap.</summary>
    Failed = 3,
    /// <summary>Exhausted the attempt cap; will not be retried.</summary>
    Dead = 4,
}
```

- [ ] **Step 4: Create `OutboxMessage.cs`** — `Entity<Guid>` + `ITenantEntity`. Operational row; **not** soft-deletable (ponytail: purge sent rows, don't tombstone).

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// A queued, self-contained notification awaiting delivery by the background drainer.
/// Bodies are rendered at enqueue time, so the drainer never touches templates.
/// </summary>
public sealed class OutboxMessage : Entity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The delivery channel (Email / Sms / Push). In-app is written directly, never via the outbox.</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>Email address / phone number / push token.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Optional subject (email).</summary>
    public string? Subject { get; set; }

    /// <summary>The final, already-rendered body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Lifecycle state.</summary>
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>Number of delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the message may be (re)attempted.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>If set, the message is held until this time (future-dated sends).</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Identifier of the drainer instance currently holding the row.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires; a past value on a <c>Sending</c> row is reclaimable.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the row was created/enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the message was successfully sent.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The last failure message, if any (never contains credentials/PII).</summary>
    public string? LastError { get; set; }
}
```

- [ ] **Step 5: Create `InAppNotification.cs`** — `AuditableEntity<Guid>` (gives `CreatedAt`) + `ITenantEntity`.

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Notifications.Entities;

/// <summary>A persisted, queryable in-app notification (written directly, not via the outbox).</summary>
public sealed class InAppNotification : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The recipient user identifier.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Short title/heading.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Rendered body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Whether the recipient has read it.</summary>
    public bool IsRead { get; set; }

    /// <summary>When it was read, if read.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
```

- [ ] **Step 6: Create `NotificationPreference.cs`**

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// Whether a channel is enabled for a tenant (and optionally a specific user),
/// plus the preferred locale. A null <see cref="UserId"/> is the tenant-wide default.
/// </summary>
public sealed class NotificationPreference : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The user this preference applies to, or null for the tenant-wide default.</summary>
    public string? UserId { get; set; }

    /// <summary>The channel this preference governs.</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>Whether the channel is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Preferred locale (e.g. "th-TH"), or null for the app default.</summary>
    public string? Locale { get; set; }
}
```

- [ ] **Step 7: Create `TenantProviderConfig.cs`** — plain-column secrets (ponytail; encryption deferred).

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Entities;

/// <summary>
/// Per-tenant provider credentials for a channel, resolved at send time with a
/// global fallback. v1 stores secrets as plain columns; encryption-at-rest is a follow-on.
/// </summary>
public sealed class TenantProviderConfig : AuditableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The channel these credentials apply to (Email / Sms).</summary>
    public NotificationChannel Channel { get; set; }

    /// <summary>SMTP host (email) — null for non-email channels.</summary>
    public string? Host { get; set; }

    /// <summary>SMTP port (email).</summary>
    public int? Port { get; set; }

    /// <summary>Provider username / API key id.</summary>
    public string? Username { get; set; }

    /// <summary>Provider password / API secret. // ponytail: plain column in v1; Data Protection later.</summary>
    public string? Password { get; set; }

    /// <summary>From-address (email) or sender id (SMS).</summary>
    public string? FromAddress { get; set; }

    /// <summary>Whether SMTP uses SSL/STARTTLS.</summary>
    public bool UseSsl { get; set; } = true;
}
```

- [ ] **Step 8: Run the test** — Run: `dotnet test tests/Themia.Modules.Notifications.Tests --filter OutboxMessageTests` — Expected: PASS.

- [ ] **Step 9: Add the new public types to `PublicAPI.Unshipped.txt`** (run `dotnet build --no-incremental`, add exactly the `RS0016`-named lines for the 5 entity types + `OutboxStatus` members).

- [ ] **Step 10: Commit**

```bash
git add src/modules/Themia.Modules.Notifications/Entities src/modules/Themia.Modules.Notifications/PublicAPI.Unshipped.txt tests/Themia.Modules.Notifications.Tests/Entities
git commit -m "feat: add Notifications module entities (outbox, in-app, preference, provider config)"
```

---

### Task 3: FluentMigrator schema across 3 engines

**Files:**
- Create: `Migrations/NotificationsSchemaMigration.cs`
- Create: `tests/Themia.Modules.Notifications.IntegrationTests/Themia.Modules.Notifications.IntegrationTests.csproj`
- Create: `tests/Themia.Modules.Notifications.IntegrationTests/SchemaMigrationTests.cs`
- Modify: `Themia.sln`

The schema tables (snake_case columns, matching the entities):
- `notifications.outbox_messages` — id (guid PK), tenant_id (nullable), channel (int), recipient, subject (nullable), body, status (int), attempts (int), next_attempt_at, scheduled_for (nullable), lease_owner (nullable), lease_expires_at (nullable), created_at, sent_at (nullable), last_error (nullable). Indexes: `(status, next_attempt_at)` for the claim query; `(tenant_id)` for isolation.
- `notifications.in_app_notifications` — id, tenant_id, user_id, title, body, is_read (bool), read_at (nullable) + auditable columns (created_at, created_by, last_modified_at, last_modified_by). Index `(tenant_id, user_id, is_read)`.
- `notifications.notification_preferences` — id, tenant_id, user_id (nullable), channel (int), is_enabled (bool), locale (nullable) + auditable columns. Index `(tenant_id, user_id, channel)`.
- `notifications.tenant_provider_configs` — id, tenant_id, channel (int), host (nullable), port (nullable int), username (nullable), password (nullable), from_address (nullable), use_ssl (bool) + auditable columns. Index `(tenant_id, channel)`.

- [ ] **Step 1: Create the IntegrationTests csproj** — copy `tests/Themia.Modules.Storage.IntegrationTests/*.csproj`; reference the module project + `Testcontainers.PostgreSql`, `Testcontainers.MsSql`, `Testcontainers.MySql`, `Npgsql`, `Microsoft.Data.SqlClient`, `MySqlConnector` (match the versions already pinned in `Directory.Packages.props`). Add `[Trait("Category","Integration")]` usage. Add to `Themia.sln`:

Run: `dotnet sln Themia.sln add tests/Themia.Modules.Notifications.IntegrationTests/Themia.Modules.Notifications.IntegrationTests.csproj`

- [ ] **Step 2: Write the failing test** — `SchemaMigrationTests.cs` (Postgres leg; the SQL Server + MySQL legs are added in Task 11's integration suite, but prove all three run here).

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Notifications.Migrations;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

[Trait("Category", "Integration")]
public class SchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync() => await container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Run_CreatesOutboxTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
        Assert.True(await TableExistsAsync("notifications.outbox_messages"));
        Assert.True(await TableExistsAsync("notifications.in_app_notifications"));
        Assert.True(await TableExistsAsync("notifications.notification_preferences"));
        Assert.True(await TableExistsAsync("notifications.tenant_provider_configs"));
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
        var second = Record.Exception(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly));
        Assert.Null(second);
    }

    private async Task<bool> TableExistsAsync(string qualified)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@n) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("n", qualified);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 3: Run it** — Run: `dotnet test tests/Themia.Modules.Notifications.IntegrationTests --filter SchemaMigrationTests` — Expected: FAIL (migration type not defined). (Requires Docker.)

- [ ] **Step 4: Create `NotificationsSchemaMigration.cs`** — `IfDatabase` per engine + unsupported-provider guard. Note MySQL has no schemas-as-namespaces; FluentMigrator maps `InSchema("notifications")` to a database/prefix per provider — keep `InSchema(SchemaName)` and let the provider handle it (Storage proves the pattern for PG/SQL Server; MySQL FluentMigrator treats schema as the database name, which the connection string already selects, so the schema call is a no-op there — acceptable).

```csharp
using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

[Migration(202606220001, "Themia.Notifications: create notifications schema and tables")]
public sealed class NotificationsSchemaMigration : Migration
{
    private const string SchemaName = "notifications";

    public override void Up()
    {
        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(CreateTables);

        // Claim-query support index uses a partial/filtered index only where supported.
        IfDatabase("postgresql").Delegate(() => CreateClaimIndex("\"notifications\".\"outbox_messages\"", "status", "next_attempt_at"));
        IfDatabase("sqlserver").Delegate(() => CreateClaimIndex("[notifications].[outbox_messages]", "status", "next_attempt_at"));
        IfDatabase("mysql").Delegate(() => CreateClaimIndex("outbox_messages", "status", "next_attempt_at"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Notifications supports only PostgreSQL, MySQL, and SQL Server. " +
                "The active database provider is not supported; add a migration branch for it."));
    }

    private void CreateTables()
    {
        if (!Schema.Schema(SchemaName).Exists())
            Create.Schema(SchemaName);

        Create.Table("outbox_messages").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("recipient").AsString(512).NotNullable()
            .WithColumn("subject").AsString(1024).Nullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable()
            .WithColumn("next_attempt_at").AsDateTimeOffset().NotNullable()
            .WithColumn("scheduled_for").AsDateTimeOffset().Nullable()
            .WithColumn("lease_owner").AsString(100).Nullable()
            .WithColumn("lease_expires_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("sent_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_error").AsString(int.MaxValue).Nullable();

        Create.Index("ix_outbox_tenant").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();

        Create.Table("in_app_notifications").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).NotNullable()
            .WithColumn("title").AsString(512).NotNullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("is_read").AsBoolean().NotNullable()
            .WithColumn("read_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_in_app_tenant_user").OnTable("in_app_notifications").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("user_id").Ascending().OnColumn("is_read").Ascending();

        Create.Table("notification_preferences").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable()
            .WithColumn("locale").AsString(20).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_pref_tenant_user_channel").OnTable("notification_preferences").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("user_id").Ascending().OnColumn("channel").Ascending();

        Create.Table("tenant_provider_configs").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("host").AsString(256).Nullable()
            .WithColumn("port").AsInt32().Nullable()
            .WithColumn("username").AsString(256).Nullable()
            .WithColumn("password").AsString(512).Nullable()
            .WithColumn("from_address").AsString(256).Nullable()
            .WithColumn("use_ssl").AsBoolean().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_provider_tenant_channel").OnTable("tenant_provider_configs").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("channel").Ascending();
    }

    private void CreateClaimIndex(string table, string statusCol, string nextCol)
    {
        // Composite index that the per-engine claim query scans (status, next_attempt_at).
        Execute.Sql($"CREATE INDEX ix_outbox_claim ON {table} ({statusCol}, {nextCol});");
    }

    public override void Down()
    {
        Delete.Table("tenant_provider_configs").InSchema(SchemaName);
        Delete.Table("notification_preferences").InSchema(SchemaName);
        Delete.Table("in_app_notifications").InSchema(SchemaName);
        Delete.Table("outbox_messages").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
```

> `body`/`last_error` as `AsString(int.MaxValue)` → `text`/`nvarchar(max)`/`longtext` per provider — verify the Storage migration's large-text pattern and match it; if Storage uses `.AsCustom(...)`, mirror that instead.

- [ ] **Step 5: Run the test** — Run: `dotnet test tests/Themia.Modules.Notifications.IntegrationTests --filter SchemaMigrationTests` — Expected: PASS.

- [ ] **Step 6: Add the migration type to `PublicAPI.Unshipped.txt`.**

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Notifications/Migrations tests/Themia.Modules.Notifications.IntegrationTests Themia.sln src/modules/Themia.Modules.Notifications/PublicAPI.Unshipped.txt
git commit -m "feat: add Notifications FluentMigrator schema (postgres/mysql/sqlserver)"
```

---

### Task 4: EF Core configuration + ApplyThemiaNotifications

**Files:**
- Create: `EntityConfiguration/OutboxMessageConfiguration.cs`, `InAppNotificationConfiguration.cs`, `NotificationPreferenceConfiguration.cs`, `TenantProviderConfigConfiguration.cs`
- Create: `EntityConfiguration/ThemiaNotificationsModelBuilderExtensions.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/EntityConfiguration/ApplyThemiaNotificationsTests.cs`

- [ ] **Step 1: Write the failing test** — uses the EF in-memory model builder only to assert the entities are mapped to the right tables/columns (no relational behavior asserted here — that is the integration suite's job).

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.EntityConfiguration;
using Xunit;

namespace Themia.Modules.Notifications.Tests.EntityConfiguration;

public class ApplyThemiaNotificationsTests
{
    private sealed class ProbeContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyThemiaNotifications();
    }

    [Fact]
    public void Maps_outbox_message_to_notifications_schema()
    {
        var options = new DbContextOptionsBuilder().UseInMemoryDatabase("probe").Options;
        using var ctx = new ProbeContext(options);
        var entity = ctx.Model.FindEntityType(typeof(OutboxMessage));
        Assert.NotNull(entity);
        Assert.Equal("outbox_messages", entity!.GetTableName());
        Assert.Equal("notifications", entity.GetSchema());
    }
}
```

> Add `Microsoft.EntityFrameworkCore.InMemory` to the **test** csproj if not already present (match the pinned version). It is a test-only dependency.

- [ ] **Step 2: Run it** — Expected: FAIL (extension/configs not defined).

- [ ] **Step 3: Create the four `IEntityTypeConfiguration<T>` classes.** Pattern for `OutboxMessageConfiguration` (repeat the same shape for the other three, mapping each property to its snake_case column and setting `.ToTable(name, "notifications")`):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.EntityConfiguration;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", "notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.Channel).HasColumnName("channel").HasConversion<int>();
        b.Property(x => x.Recipient).HasColumnName("recipient").HasMaxLength(512).IsRequired();
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(1024);
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        b.Property(x => x.Attempts).HasColumnName("attempts");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(x => x.ScheduledFor).HasColumnName("scheduled_for");
        b.Property(x => x.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(100);
        b.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.LastError).HasColumnName("last_error");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_outbox_claim");
    }
}
```

> `TenantId` is `TenantId?` (a value type wrapper). Match how Storage's EF config maps `TenantId` — if Storage uses a `ValueConverter` (e.g. `TenantId` ⇆ string), copy that exact converter here for every `tenant_id` property. Do **not** invent a new conversion.

For `InAppNotificationConfiguration`, `NotificationPreferenceConfiguration`, `TenantProviderConfigConfiguration`: same structure, mapping the auditable columns (`created_at`, `created_by`, `last_modified_at`, `last_modified_by`) too. Full code (each ~20-30 lines) — write them out completely, no "similar to above".

- [ ] **Step 4: Create `ThemiaNotificationsModelBuilderExtensions.cs`**

```csharp
using Microsoft.EntityFrameworkCore;

namespace Themia.Modules.Notifications.EntityConfiguration;

/// <summary>Applies the Notifications module's EF Core entity configurations.</summary>
public static class ThemiaNotificationsModelBuilderExtensions
{
    /// <summary>
    /// Registers the outbox, in-app, preference, and provider-config entities on the given model.
    /// Call from your <c>ThemiaDbContext.OnModelCreating</c>; the base context applies tenant and
    /// soft-delete query filters to entities implementing the framework marker interfaces.
    /// </summary>
    public static ModelBuilder ApplyThemiaNotifications(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InAppNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationPreferenceConfiguration());
        modelBuilder.ApplyConfiguration(new TenantProviderConfigConfiguration());
        return modelBuilder;
    }
}
```

- [ ] **Step 5: Run the test** — Expected: PASS.

- [ ] **Step 6: Add `ThemiaNotificationsModelBuilderExtensions` to `PublicAPI.Unshipped.txt`** (the configs are `internal`).

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: add EF Core configuration and ApplyThemiaNotifications for the module entities"
```

---

### Task 5: Dapper mappings

**Files:**
- Create: `Mapping/NotificationsDapperMappings.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Mapping/NotificationsDapperMappingsTests.cs`

- [ ] **Step 1: Inspect Storage's `StorageDapperMappings.Apply`** to learn the exact `EntityMapping` API (table name, schema, column mapping registration). Match it.

- [ ] **Step 2: Write the failing test** — assert the registry resolves the outbox mapping to the right table/column names after `Apply`.

```csharp
using Themia.Framework.Data.Dapper.Mapping; // adjust to the actual namespace of EntityMappingRegistry
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Mapping;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Mapping;

public class NotificationsDapperMappingsTests
{
    [Fact]
    public void Apply_MapsOutboxStatusColumn()
    {
        var registry = new EntityMappingRegistry();
        NotificationsDapperMappings.Apply(registry);
        var mapping = registry.For<OutboxMessage>();
        Assert.Equal("status", mapping.Column(nameof(OutboxMessage.Status)));
        Assert.Equal("next_attempt_at", mapping.Column(nameof(OutboxMessage.NextAttemptAt)));
    }
}
```

> The exact `EntityMapping` construction API and `Column(...)` accessor must be copied from `StorageDapperMappings` — the snippet above assumes a `Column(propertyName)` accessor that the Storage test also uses. If Storage builds mappings differently (e.g. fluent `EntityMapping.For<T>().Table(...).Schema(...).Map(x => x.Prop, "col")`), use that exact form.

- [ ] **Step 3: Run it** — Expected: FAIL.

- [ ] **Step 4: Create `NotificationsDapperMappings.cs`** — register all four entities with snake_case columns + schema `notifications`, following the Storage form verbatim. Map `TenantId`, `Channel`, `Status` with the same converters the EF config uses (so both peers read/write identical column values). Full code, all four entities.

- [ ] **Step 5: Run the test** — Expected: PASS.

- [ ] **Step 6: Add `NotificationsDapperMappings` to `PublicAPI.Unshipped.txt`.**

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: add Dapper entity mappings for Notifications module"
```

---

### Task 6: Stores for in-app / preferences / provider config (both peers)

These three entities are read/written through ordinary tenant-isolated stores (the outbox is special — Task 7+). Build the interfaces + EF and Dapper implementations.

**Files:**
- Create: `Stores/IInAppNotificationStore.cs` + `EfInAppNotificationStore.cs` + `DapperInAppNotificationStore.cs`
- Create: `Stores/INotificationPreferenceStore.cs` + `Ef...` + `Dapper...`
- Create: `Stores/ITenantProviderConfigStore.cs` + `Ef...` + `Dapper...`
- Test: `tests/Themia.Modules.Notifications.IntegrationTests/InAppNotificationStoreTests.cs` (round-trip on Postgres)

- [ ] **Step 1: Define `IInAppNotificationStore`**

```csharp
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Reads and writes in-app notifications for the current tenant.</summary>
public interface IInAppNotificationStore
{
    /// <summary>Persists a new in-app notification.</summary>
    Task AddAsync(InAppNotification notification, CancellationToken ct = default);

    /// <summary>Returns a user's notifications, newest first.</summary>
    Task<IReadOnlyList<InAppNotification>> ListForUserAsync(string userId, bool unreadOnly, CancellationToken ct = default);

    /// <summary>Marks a notification read; returns false if not found for the current tenant.</summary>
    Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default);
}
```

`INotificationPreferenceStore`: `Task<IReadOnlyList<NotificationPreference>> ListAsync(string? userId, CancellationToken)` + `Task UpsertAsync(NotificationPreference, CancellationToken)`.
`ITenantProviderConfigStore`: `Task<TenantProviderConfig?> FindAsync(NotificationChannel channel, CancellationToken)` + `Task UpsertAsync(TenantProviderConfig, CancellationToken)`.

- [ ] **Step 2: Implement the EF stores** — inject the adopter's `ThemiaDbContext` (resolve `DbContext` via the framework's registered context; follow how Storage's `EfStorageObjectStore` obtains its context — likely a constructor `ThemiaDbContext` or a typed `DbContext`). Tenant + soft-delete filtering is automatic via the base context's query filters; do **not** re-filter by tenant in the store. Use `AsNoTracking()` for reads. Example `EfInAppNotificationStore`:

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.EFCore;       // ThemiaDbContext
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Stores;

internal sealed class EfInAppNotificationStore(ThemiaDbContext context) : IInAppNotificationStore
{
    public async Task AddAsync(InAppNotification notification, CancellationToken ct = default)
    {
        await context.Set<InAppNotification>().AddAsync(notification, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<InAppNotification>> ListForUserAsync(string userId, bool unreadOnly, CancellationToken ct = default)
    {
        var q = context.Set<InAppNotification>().AsNoTracking().Where(x => x.UserId == userId);
        if (unreadOnly) q = q.Where(x => !x.IsRead);
        return await q.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await context.Set<InAppNotification>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return false;
        entity.IsRead = true;
        entity.ReadAt = DateTimeOffset.UtcNow; // ponytail: store-set timestamp; TimeProvider injected in Task 14 wiring if needed
        await context.SaveChangesAsync(ct);
        return true;
    }
}
```

> Confirm the exact `ThemiaDbContext` injection pattern from Storage. If Storage resolves a `DbContext` base (not the concrete `ThemiaDbContext`), match that. The CreatedAt/audit stamping is applied by the framework's `SaveChanges` interceptor (Storage relies on it) — do not stamp manually except the `ReadAt` shown.

- [ ] **Step 3: Implement the Dapper stores** — use `ITenantQueryFactory.For<T>()` (pre-seeds the tenant predicate) for reads, and the Dapper UoW / pending-op sink for writes, exactly as Storage's `DapperStorageObjectStore` does. Full code for all three.

- [ ] **Step 4: Write the integration test** (`InAppNotificationStoreTests`, Postgres) — migrate, build a DI scope with EF peer + a fixed tenant, `AddAsync`, then `ListForUserAsync` returns it; `MarkReadAsync` flips the flag; a different tenant sees nothing. Reuse the Storage integration fixture pattern for building the scoped provider with a tenant context.

- [ ] **Step 5: Run** — `dotnet test tests/Themia.Modules.Notifications.IntegrationTests --filter InAppNotificationStoreTests` — Expected: PASS.

- [ ] **Step 6: Add the three store interfaces to `PublicAPI.Unshipped.txt`** (impls are internal).

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: add in-app/preference/provider-config stores (EF + Dapper peers)"
```

---

# Phase B — Transactional outbox + near-real-time drainer

### Task 7: IOutboxStore — transactional enqueue (both peers)

**Files:**
- Create: `Outbox/IOutboxStore.cs`, `Outbox/OutboxStore.cs`
- Test: `tests/Themia.Modules.Notifications.IntegrationTests/OutboxEnqueueTransactionTests.cs`

The contract: `EnqueueAsync` stages the insert in the **caller's** unit of work so the row commits atomically with the triggering work. It uses the peer-agnostic `IRepository<OutboxMessage, Guid>.AddAsync` (which stamps `TenantId`) and does **NOT** call `SaveChanges` — the caller's `IUnitOfWork.SaveChangesAsync` commits it (rollback-safety). A single `OutboxStore` works over EF or Dapper (whichever the adopter registered). No `EfOutboxStore`/`DapperOutboxStore` split (see the Conventions "Data-access seam" note). A separate `EnqueueAndSaveAsync` convenience is **not** added (YAGNI; the dispatcher and tests drive `SaveChanges`).

- [ ] **Step 1: Define `IOutboxStore`**

```csharp
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Stages outbox messages into the caller's current unit of work, so a queued notification
/// commits atomically with the work that triggered it (no "sent but rolled back").
/// </summary>
public interface IOutboxStore
{
    /// <summary>Stages an insert of <paramref name="message"/>; the caller's UoW commit persists it.</summary>
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing integration test** — prove rollback-safety on Postgres. Reuse the Task 6 integration-test bootstrap (`tests/.../InAppNotificationStoreTests.cs` / `TestNotificationsDbContext.cs`) for building a tenant-scoped EF service provider. Resolve `IOutboxStore` + `IUnitOfWork` from the scope; the "did it persist" assertion counts rows via a direct query helper (as Task 6's test does).

```csharp
// EnqueueAsync stages but does NOT commit; without SaveChanges the row must not persist.
[Fact]
public async Task Enqueue_without_save_does_not_persist()
{
    await using var scope = BuildScope();   // tenant-scoped EF provider (Task 6 bootstrap)
    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
    await store.EnqueueAsync(NewEmail("a@example.com"));
    // no SaveChanges -> dispose discards
    await scope.DisposeAsync();
    Assert.Equal(0, await CountOutboxAsync());
}

[Fact]
public async Task Enqueue_then_save_persists_the_row()
{
    await using var scope = BuildScope();
    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
    await store.EnqueueAsync(NewEmail("a@example.com"));
    await uow.SaveChangesAsync();
    Assert.Equal(1, await CountOutboxAsync());
}
```

> `NewEmail(addr)` builds a pending `OutboxMessage` (Channel=Email, Status=Pending, Attempts=0, NextAttemptAt=now, CreatedAt=now) and calls `SetId(Guid.NewGuid())`.

- [ ] **Step 3: Implement `OutboxStore`** — single, peer-agnostic, repository-backed (mirrors `TenantStorage`'s constructor injection):

```csharp
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Outbox;

internal sealed class OutboxStore(IRepository<OutboxMessage, Guid> repository) : IOutboxStore
{
    // Stages the insert (repository stamps TenantId); the caller's IUnitOfWork.SaveChangesAsync commits it (rollback-safe).
    public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return repository.AddAsync(message, ct);
    }
}
```

> Confirm `IRepository<T, Guid>.AddAsync` signature against `src/framework/Themia.Framework.Data.Abstractions/Repositories/IRepository.cs` — if `AddAsync` itself flushes (some repos do), instead inject the repository's staging method. The contract that MUST hold: after `EnqueueAsync` without a `SaveChangesAsync`, nothing is persisted (the rollback test proves it). If `AddAsync` auto-saves, adjust to the staging API the framework exposes and update the test's expectation, but keep rollback-safety — flag it if the framework offers no stage-without-save path.

- [ ] **Step 4: Run** — Expected: both tests PASS.

- [ ] **Step 5: PublicAPI (`IOutboxStore`) + Commit**

```bash
git commit -am "feat: add transactional IOutboxStore (repository-backed, peer-agnostic)"
```

---

### Task 8: BackoffPolicy (pure logic)

**Files:**
- Create: `Outbox/BackoffPolicy.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Outbox/BackoffPolicyTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Themia.Modules.Notifications.Outbox;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

public class BackoffPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(1, 2)]     // attempt 1 -> ~2s
    [InlineData(2, 4)]     // attempt 2 -> ~4s
    [InlineData(3, 8)]     // attempt 3 -> ~8s
    public void NextAttempt_grows_exponentially(int attempts, int expectedSeconds)
    {
        var next = BackoffPolicy.NextAttemptAt(Now, attempts, maxAttempts: 5);
        Assert.Equal(Now.AddSeconds(expectedSeconds), next);
    }

    [Fact]
    public void NextAttempt_is_capped()
    {
        var next = BackoffPolicy.NextAttemptAt(Now, attempts: 20, maxAttempts: 50);
        Assert.True(next - Now <= BackoffPolicy.MaxDelay);
    }

    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void IsDead_when_attempts_reach_cap(int attempts, int max, bool expected)
        => Assert.Equal(expected, BackoffPolicy.IsDead(attempts, max));
}
```

- [ ] **Step 2: Run it** — Expected: FAIL.

- [ ] **Step 3: Implement `BackoffPolicy`**

```csharp
namespace Themia.Modules.Notifications.Outbox;

/// <summary>Exponential backoff and the dead-letter decision for outbox retries.</summary>
internal static class BackoffPolicy
{
    /// <summary>Maximum delay between attempts (cap on the exponential growth).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Next attempt time: <c>now + min(2^attempts seconds, MaxDelay)</c>.</summary>
    public static DateTimeOffset NextAttemptAt(DateTimeOffset now, int attempts, int maxAttempts)
    {
        var seconds = Math.Pow(2, Math.Min(attempts, 20)); // guard overflow
        var delay = TimeSpan.FromSeconds(seconds);
        if (delay > MaxDelay) delay = MaxDelay;
        return now + delay;
    }

    /// <summary>True when attempts have reached the configured cap.</summary>
    public static bool IsDead(int attempts, int maxAttempts) => attempts >= maxAttempts;
}
```

- [ ] **Step 4: Run** — Expected: PASS. (`internal`, no PublicAPI entry.)

- [ ] **Step 5: Commit**

```bash
git commit -am "feat: add exponential backoff policy for the outbox drainer"
```

---

### Task 9: INotificationsSqlDialect + 3 engine dialects (atomic claim)

This is the correctness core. Each dialect provides the skip-locked claim SQL, the complete/fail/reclaim SQL, and a connection factory — modeled on `IExceptionalSqlDialect`.

**Files:**
- Create: `Outbox/ClaimedOutboxRow.cs`, `Outbox/INotificationsSqlDialect.cs`
- Create: `Outbox/PostgresNotificationsDialect.cs`, `MySqlNotificationsDialect.cs`, `SqlServerNotificationsDialect.cs`
- Test (integration): `tests/Themia.Modules.Notifications.IntegrationTests/OutboxClaimConcurrencyTests.cs`

- [ ] **Step 1: Define `ClaimedOutboxRow`** (the columns the drainer needs to send + update)

```csharp
using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>A row claimed from the outbox for delivery.</summary>
internal sealed record ClaimedOutboxRow(
    Guid Id,
    string? TenantId,
    NotificationChannel Channel,
    string Recipient,
    string? Subject,
    string Body,
    int Attempts);
```

- [ ] **Step 2: Define `INotificationsSqlDialect`**

```csharp
using System.Data.Common;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Engine-specific SQL for the outbox drainer. The drainer uses its own connection
/// (it serves all tenants), so this bypasses the tenant filter by design — the
/// sanctioned data-layer raw-connection path.
/// </summary>
internal interface INotificationsSqlDialect
{
    /// <summary>Opens a new connection to the drain database.</summary>
    DbConnection CreateConnection();

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due rows (status pending/failed, or a
    /// stale-lease sending row), marking them <c>sending</c> with the given lease, and returns them.
    /// </summary>
    Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct);

    /// <summary>Marks a claimed row as sent.</summary>
    Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset sentAt, CancellationToken ct);

    /// <summary>Records a failed attempt: bumps attempts, sets next_attempt_at + last_error, status failed or dead.</summary>
    Task FailAsync(DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct);
}
```

- [ ] **Step 3: Implement `PostgresNotificationsDialect`** — `FOR UPDATE SKIP LOCKED` in a transaction. Use Dapper (already referenced transitively via the framework Dapper package) or raw `DbCommand`. Parameterized.

```csharp
using System.Data.Common;
using Dapper;
using Npgsql;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

internal sealed class PostgresNotificationsDialect(string connectionString) : INotificationsSqlDialect
{
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    public async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        DbConnection connection, string leaseOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt,
        int batchSize, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        const string selectSql = """
            SELECT id FROM notifications.outbox_messages
            WHERE next_attempt_at <= @now
              AND (scheduled_for IS NULL OR scheduled_for <= @now)
              AND ( status IN (0, 3)                       -- pending, failed
                    OR (status = 1 AND lease_expires_at < @now) )  -- stale sending lease
            ORDER BY next_attempt_at
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
            """;
        var ids = (await connection.QueryAsync<Guid>(
            new CommandDefinition(selectSql, new { now, batch = batchSize }, tx, cancellationToken: ct))).AsList();
        if (ids.Count == 0) { await tx.CommitAsync(ct); return Array.Empty<ClaimedOutboxRow>(); }

        const string updateSql = """
            UPDATE notifications.outbox_messages
            SET status = 1, lease_owner = @owner, lease_expires_at = @exp
            WHERE id = ANY(@ids)
            RETURNING id, tenant_id, channel, recipient, subject, body, attempts
            """;
        var rows = await connection.QueryAsync<(Guid, string?, int, string, string?, string, int)>(
            new CommandDefinition(updateSql, new { owner = leaseOwner, exp = leaseExpiresAt, ids = ids.ToArray() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rows.Select(r => new ClaimedOutboxRow(r.Item1, r.Item2, (NotificationChannel)r.Item3, r.Item4, r.Item5, r.Item6, r.Item7)).ToList();
    }

    public Task CompleteAsync(DbConnection connection, Guid id, DateTimeOffset sentAt, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notifications.outbox_messages SET status = 2, sent_at = @sentAt, lease_owner = NULL, lease_expires_at = NULL WHERE id = @id",
            new { id, sentAt }, cancellationToken: ct));

    public Task FailAsync(DbConnection connection, Guid id, int attempts, DateTimeOffset nextAttemptAt,
        bool dead, string error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE notifications.outbox_messages
            SET status = @status, attempts = @attempts, next_attempt_at = @next, last_error = @error,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id
            """, new { id, status = dead ? 4 : 3, attempts, next = nextAttemptAt, error }, cancellationToken: ct));
}
```

> Status integer literals (0 pending, 1 sending, 2 sent, 3 failed, 4 dead) match `OutboxStatus`. Keep them as a single internal `const` set if the reviewer prefers — but the enum values are stable and documented.

- [ ] **Step 4: Implement `MySqlNotificationsDialect`** — same shape, `MySqlConnector`'s `MySqlConnection`, `FOR UPDATE SKIP LOCKED` (MySQL 8.0+/MariaDB 10.6+), `WHERE id IN @ids` (MySqlConnector expands the list), `LIMIT @batch`. MySQL `UPDATE ... RETURNING` is unavailable, so: select-and-lock ids, `UPDATE ... SET status=1,... WHERE id IN @ids`, then `SELECT ... WHERE id IN @ids` for the claimed rows — all inside the one transaction. Full code.

- [ ] **Step 5: Implement `SqlServerNotificationsDialect`** — single-statement claim with `OUTPUT`:

```csharp
const string claimSql = """
    UPDATE TOP (@batch) notifications.outbox_messages WITH (READPAST, UPDLOCK, ROWLOCK)
    SET status = 1, lease_owner = @owner, lease_expires_at = @exp
    OUTPUT inserted.id, inserted.tenant_id, inserted.channel, inserted.recipient,
           inserted.subject, inserted.body, inserted.attempts
    WHERE next_attempt_at <= @now
      AND (scheduled_for IS NULL OR scheduled_for <= @now)
      AND ( status IN (0, 3) OR (status = 1 AND lease_expires_at < @now) )
    """;
```

`Microsoft.Data.SqlClient`'s `SqlConnection`; no explicit transaction needed (single atomic statement). Full code with `CompleteAsync`/`FailAsync`.

- [ ] **Step 6: Write the concurrency integration test** (`OutboxClaimConcurrencyTests`, Postgres + SQL Server + MySQL via `[Theory]`/per-engine fixtures) — insert N pending rows, run **two** dialect `ClaimAsync` calls on **separate connections concurrently**, assert the union of claimed ids has **no overlap** and total ≤ N (no double-claim). Also: a `scheduled_for` future row is not claimed; a stale-lease `sending` row IS reclaimed.

- [ ] **Step 7: Run** — `dotnet test tests/Themia.Modules.Notifications.IntegrationTests --filter OutboxClaimConcurrencyTests` — Expected: PASS on all engines (Docker required).

- [ ] **Step 8: Commit** (`internal` types — no PublicAPI changes)

```bash
git commit -am "feat: add per-engine atomic outbox claim dialects (skip-locked)"
```

---

### Task 10: DrainSignal + OutboxDrainer (BackgroundService)

**Files:**
- Create: `Outbox/DrainSignal.cs`, `Outbox/OutboxDrainer.cs`
- Test (unit): `tests/Themia.Modules.Notifications.Tests/Outbox/DrainSignalTests.cs`
- Test (integration): `tests/Themia.Modules.Notifications.IntegrationTests/OutboxRoundTripTests.cs`

- [ ] **Step 1: Write the failing `DrainSignal` test**

```csharp
using Themia.Modules.Notifications.Outbox;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Outbox;

public class DrainSignalTests
{
    [Fact]
    public async Task WaitAsync_completes_after_Signal()
    {
        var signal = new DrainSignal();
        var wait = signal.WaitAsync(TestContext.Current.CancellationToken);
        signal.Signal();
        await wait; // does not hang
    }

    [Fact]
    public async Task WaitAsync_completes_when_already_signaled()
    {
        var signal = new DrainSignal();
        signal.Signal();
        await signal.WaitAsync(TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Implement `DrainSignal`** — an in-process coalescing wake, backed by `Channel<bool>` (bounded 1, drop-write) or a reset `SemaphoreSlim`. Coalescing: many signals between drains collapse to one wake.

```csharp
using System.Threading.Channels;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// In-process wake for the drainer, kicked after an enqueuing transaction commits. Coalescing:
/// repeated signals before the next drain collapse to a single wake. In-process only — in a
/// multi-instance deployment, cross-instance latency is bounded by the poll interval.
/// </summary>
public sealed class DrainSignal
{
    private readonly Channel<bool> channel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Wakes the drainer (non-blocking; coalesces with any pending signal).</summary>
    public void Signal() => channel.Writer.TryWrite(true);

    /// <summary>Completes when a signal is available or the token cancels.</summary>
    public async Task WaitAsync(CancellationToken ct) => await channel.Reader.ReadAsync(ct);
}
```

- [ ] **Step 3: Run the `DrainSignal` test** — Expected: PASS.

- [ ] **Step 4: Implement `OutboxDrainer : BackgroundService`** — singleton; resolves the dialect + a scoped sender set per cycle; signaled wake OR poll interval; claim → dispatch by channel → complete/fail with backoff. Resolve `IServiceScopeFactory` (the drainer is a singleton; senders/config resolver are scoped). The dialect is registered as a singleton built from the connection string (Task 14). Lease owner = a stable per-instance id.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Modules.Notifications.Config;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

internal sealed class OutboxDrainer(
    INotificationsSqlDialect dialect,
    DrainSignal signal,
    IServiceScopeFactory scopeFactory,
    NotificationsModuleOptions options,
    TimeProvider time,
    ILogger<OutboxDrainer> logger) : BackgroundService
{
    private readonly string leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int drained;
                do { drained = await DrainOnceAsync(stoppingToken); }
                while (drained == options.MaxBatchSize && !stoppingToken.IsCancellationRequested); // keep draining a full batch

                // Wait for the next signal OR the poll interval, whichever comes first.
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pollCts.CancelAfter(TimeSpan.FromSeconds(options.DrainIntervalSeconds));
                try { await signal.WaitAsync(pollCts.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* poll tick */ }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox drain cycle failed; backing off before retry.");
                try { await Task.Delay(TimeSpan.FromSeconds(options.DrainIntervalSeconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var leaseExpires = now.AddSeconds(options.LeaseSeconds);
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(ct);

        var claimed = await dialect.ClaimAsync(connection, leaseOwner, now, leaseExpires, options.MaxBatchSize, ct);
        if (claimed.Count == 0) return 0;

        using var scope = scopeFactory.CreateScope();
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(scope.ServiceProvider, connection, row, ct);
        }
        return claimed.Count;
    }

    private async Task DeliverAsync(IServiceProvider sp, System.Data.Common.DbConnection connection, ClaimedOutboxRow row, CancellationToken ct)
    {
        try
        {
            var message = new NotificationMessage
            {
                Channel = row.Channel,
                Recipient = row.Recipient,
                Subject = row.Subject,
                Body = row.Body, // already rendered at enqueue
            };
            var sender = ResolveSender(sp, row.Channel);
            var result = await sender.SendAsync(message, ct);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error ?? "Sender reported failure.");

            await dialect.CompleteAsync(connection, row.Id, time.GetUtcNow(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (FormatException ex)
        {
            // ponytail: malformed address/body is permanent — do not retry, mark dead immediately.
            await FailRowAsync(connection, row, permanent: true, ex, ct);
        }
        catch (Exception ex)
        {
            await FailRowAsync(connection, row, permanent: false, ex, ct);
        }
    }

    private async Task FailRowAsync(System.Data.Common.DbConnection connection, ClaimedOutboxRow row, bool permanent, Exception ex, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        var dead = permanent || BackoffPolicy.IsDead(attempts, options.MaxAttempts);
        var next = BackoffPolicy.NextAttemptAt(time.GetUtcNow(), attempts, options.MaxAttempts);
        // Log once, with safe context only (no recipient PII, no credentials).
        logger.LogWarning(ex, "Notification {Id} on {Channel} failed (attempt {Attempts}); {Outcome}.",
            row.Id, row.Channel, attempts, dead ? "dead-lettered" : "will retry");
        await dialect.FailAsync(connection, row.Id, attempts, next, dead, Truncate(ex.Message, 1000), ct);
    }

    private static IDispatchSender ResolveSender(IServiceProvider sp, NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => new SenderAdapter(sp.GetRequiredService<IEmailSender>()),
        NotificationChannel.Sms => new SenderAdapter(sp.GetRequiredService<ISmsSender>()),
        NotificationChannel.Push => new SenderAdapter(sp.GetRequiredService<IPushSender>()),
        _ => throw new NotSupportedException($"Channel {channel} is not deliverable via the outbox."),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // Small adapter so the switch returns one delegate-like type. // ponytail: avoids three near-identical branches.
    private interface IDispatchSender { Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct); }
    private sealed class SenderAdapter(object sender) : IDispatchSender
    {
        public Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct) => sender switch
        {
            IEmailSender e => e.SendAsync(m, ct),
            ISmsSender s => s.SendAsync(m, ct),
            IPushSender p => p.SendAsync(m, ct),
            _ => throw new NotSupportedException(),
        };
    }
}
```

> The `SenderAdapter` indirection is ugly — during the code-quality review, prefer replacing it with a direct `switch` that awaits the right sender inline (three short branches). Keep whichever the reviewer finds clearer; functionality is identical. (Flagged so the implementer doesn't treat the adapter as sacred.)
>
> Per-tenant provider config: in v1 the registered `IEmailSender`/`ISmsSender` are the senders. `IProviderConfigResolver` (Task 14) lets a sender pick per-tenant creds; the drainer passes `row.TenantId` through `NotificationMessage.Metadata` if a sender needs it. Keep the drainer agnostic — it just calls the registered sender. (Per-tenant credential selection inside `SmtpEmailSender` is wired in Task 14; if no per-tenant row exists, the global `SmtpEmailOptions` is used.)

- [ ] **Step 5: Write the round-trip integration test** (`OutboxRoundTripTests`, all 3 engines) — register a fake `IEmailSender` that records sends; enqueue+commit one email row; run the drainer's `DrainOnceAsync` (expose `internal` or drive via `StartAsync` + signal + a short await); assert the row becomes `sent` and the fake recorded it. A failing fake → row goes `failed` then `dead` after `MaxAttempts`; `next_attempt_at` advances.

- [ ] **Step 6: Run** — Expected: PASS.

- [ ] **Step 7: Add `DrainSignal` to `PublicAPI.Unshipped.txt`** (it is public so apps can kick it post-commit; drainer is internal).

- [ ] **Step 8: Commit**

```bash
git commit -am "feat: add DrainSignal and OutboxDrainer background service"
```

---

# Phase C — Dispatcher, preferences, in-app, config, wiring

### Task 11: PreferenceResolver

**Files:**
- Create: `Dispatch/IPreferenceResolver.cs`, `Dispatch/PreferenceResolver.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Dispatch/PreferenceResolverTests.cs`

Resolution rules: per-user preference overrides the tenant-wide default (null UserId); absence of any row = channel enabled (opt-out model). Returns the enabled channels (+ resolved locale) for a (userId, requestedChannels) pair.

- [ ] **Step 1: Write the failing test** — feed an in-memory `INotificationPreferenceStore` fake: tenant default disables SMS; user X re-enables SMS; resolve for user X over {Email, Sms} → both enabled; for user Y → only Email. Locale: user row wins over tenant row.

- [ ] **Step 2: Define `IPreferenceResolver`**

```csharp
using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

/// <summary>The channels (and locale) enabled for a recipient after applying preferences.</summary>
public sealed record ResolvedPreferences(IReadOnlyList<NotificationChannel> EnabledChannels, string? Locale);

/// <summary>Resolves enabled channels and locale for a recipient from stored preferences.</summary>
public interface IPreferenceResolver
{
    /// <summary>Filters <paramref name="requested"/> to the channels enabled for <paramref name="userId"/>.</summary>
    Task<ResolvedPreferences> ResolveAsync(string userId, IReadOnlyList<NotificationChannel> requested, CancellationToken ct = default);
}
```

- [ ] **Step 3: Implement `PreferenceResolver`** (internal) over `INotificationPreferenceStore`. Opt-out default; user row > tenant row.

- [ ] **Step 4: Run** — Expected: PASS.

- [ ] **Step 5: PublicAPI (`IPreferenceResolver` + `ResolvedPreferences`) + Commit**

```bash
git commit -am "feat: add notification preference resolver"
```

---

### Task 12: INotificationDispatcher

**Files:**
- Create: `Dispatch/NotificationRequest.cs`, `Dispatch/INotificationDispatcher.cs`, `Dispatch/NotificationDispatcher.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Dispatch/NotificationDispatcherTests.cs`

Behavior: for each enabled **external** channel (Email/Sms/Push) → render body (if template) → enqueue one `OutboxMessage` via `IOutboxStore`. For **InApp** → write directly via `IInAppNotificationStore`. Rendering uses the core `INotificationTemplateRenderer`. The dispatcher does **not** call SaveChanges — it stages within the caller's UoW (so it's atomic with the triggering work); the caller (or a mediator `TransactionBehavior`) commits. Document this contract.

- [ ] **Step 1: Define `NotificationRequest`**

```csharp
using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

/// <summary>An app's request to notify a recipient across one or more channels.</summary>
public sealed class NotificationRequest
{
    /// <summary>Recipient user id (for preference resolution and in-app).</summary>
    public required string UserId { get; init; }

    /// <summary>Channels to attempt (subject to preferences).</summary>
    public required IReadOnlyList<NotificationChannel> Channels { get; init; }

    /// <summary>Email address / phone / push token, by channel. In-app ignores this.</summary>
    public IReadOnlyDictionary<NotificationChannel, string>? Recipients { get; init; }

    /// <summary>Subject (email / in-app title).</summary>
    public string? Subject { get; init; }

    /// <summary>Pre-rendered body, or null to render Template+Model.</summary>
    public string? Body { get; init; }

    /// <summary>Handlebars template source (used when Body is null).</summary>
    public string? Template { get; init; }

    /// <summary>Template model.</summary>
    public object? Model { get; init; }

    /// <summary>Optional future-send time (outbox only).</summary>
    public DateTimeOffset? ScheduledFor { get; init; }
}
```

- [ ] **Step 2: Define `INotificationDispatcher`**

```csharp
namespace Themia.Modules.Notifications.Dispatch;

/// <summary>
/// Routes a notification request to its channels: external channels are enqueued on the outbox
/// (delivered by the drainer); in-app is written directly. Staged in the caller's unit of work —
/// commit (or a mediator transaction behavior) persists it atomically with the triggering work.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Dispatches the request after applying recipient preferences.</summary>
    Task DispatchAsync(NotificationRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the failing test** — fakes for `IPreferenceResolver`, `IOutboxStore`, `IInAppNotificationStore`, `INotificationTemplateRenderer`, `ITenantContext`, `TimeProvider`. Assert:
  - Email+Sms request with both enabled → 2 outbox rows enqueued, bodies rendered (Template+Model), `NextAttemptAt == now` (or `ScheduledFor`), status pending.
  - InApp channel → no outbox row, one `InAppNotification` written with title=Subject.
  - A channel disabled by preference → not enqueued.
  - Body provided verbatim → renderer not called.

- [ ] **Step 4: Implement `NotificationDispatcher`** (internal)

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

internal sealed class NotificationDispatcher(
    IPreferenceResolver preferences,
    IOutboxStore outbox,
    IInAppNotificationStore inApp,
    INotificationTemplateRenderer renderer,
    ITenantContext tenantContext,
    TimeProvider time) : INotificationDispatcher
{
    public async Task DispatchAsync(NotificationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await preferences.ResolveAsync(request.UserId, request.Channels, ct);
        var body = request.Body ?? (request.Template is null ? string.Empty : renderer.Render(request.Template, request.Model ?? new object()));
        var now = time.GetUtcNow();
        var tenant = tenantContext.CurrentTenantId;

        foreach (var channel in resolved.EnabledChannels)
        {
            if (channel == NotificationChannel.InApp)
            {
                var notification = new InAppNotification
                {
                    TenantId = tenant,
                    UserId = request.UserId,
                    Title = request.Subject ?? string.Empty,
                    Body = body,
                    CreatedAt = now,
                };
                notification.SetId(Guid.NewGuid()); // framework: Entity<TId>.Id has a protected setter; use SetId
                await inApp.AddAsync(notification, ct);
                continue;
            }

            var recipient = request.Recipients?.GetValueOrDefault(channel);
            if (string.IsNullOrWhiteSpace(recipient)) continue; // no address for this channel
            var message = new OutboxMessage
            {
                TenantId = tenant,
                Channel = channel,
                Recipient = recipient,
                Subject = request.Subject,
                Body = body,
                Status = OutboxStatus.Pending,
                Attempts = 0,
                NextAttemptAt = request.ScheduledFor ?? now,
                ScheduledFor = request.ScheduledFor,
                CreatedAt = now,
            };
            message.SetId(Guid.NewGuid());
            await outbox.EnqueueAsync(message, ct);
        }
    }
}
```

> `InAppNotificationStore.AddAsync` in Task 6 calls `SaveChanges`, which would break the "stage in caller's UoW" contract. **Resolve this in this task:** split the in-app store into a stage-only `Add` (no SaveChanges) used by the dispatcher, OR have the dispatcher use the EF `DbContext` / Dapper UoW directly. Simplest: change `IInAppNotificationStore.AddAsync` to stage only (remove its `SaveChanges`), and make the standalone in-app write API (for app code that wants immediate persistence) a separate `AddAndSaveAsync`. Update Task 6's tests accordingly. Pick one and keep the whole module consistent — the dispatcher must not commit.

- [ ] **Step 5: Run** — Expected: PASS.

- [ ] **Step 6: PublicAPI (`NotificationRequest`, `INotificationDispatcher`) + Commit**

```bash
git commit -am "feat: add notification dispatcher (outbox for external, direct for in-app)"
```

---

### Task 13: Per-tenant provider config resolver

**Files:**
- Create: `Config/IProviderConfigResolver.cs`, `Config/ProviderConfigResolver.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/Config/ProviderConfigResolverTests.cs`

Resolves a `TenantProviderConfig?` for the current tenant+channel via `ITenantProviderConfigStore`, falling back to `null` (caller uses its globally-registered options). This is the seam a custom `SmtpEmailSender` wrapper uses to pick per-tenant creds.

- [ ] **Step 1: Write the failing test** — store has a row for the current tenant+Email → resolver returns it; no row → returns null.

- [ ] **Step 2: Define + implement `IProviderConfigResolver`/`ProviderConfigResolver`**

```csharp
using Themia.Modules.Notifications.Entities;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Config;

/// <summary>Resolves per-tenant provider credentials for a channel, or null to use the global config.</summary>
public interface IProviderConfigResolver
{
    /// <summary>Returns the current tenant's config for the channel, or null if none is set.</summary>
    Task<TenantProviderConfig?> ResolveAsync(NotificationChannel channel, CancellationToken ct = default);
}
```

- [ ] **Step 3: Run** — Expected: PASS.

- [ ] **Step 4: PublicAPI + Commit**

```bash
git commit -am "feat: add per-tenant provider config resolver"
```

---

### Task 14: NotificationsModule + DI wiring

**Files:**
- Create: `NotificationsModule.cs`
- Create: `DependencyInjection/NotificationsServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Notifications.Tests/DependencyInjection/AddThemiaNotificationsModuleTests.cs`

- [ ] **Step 1: Write the failing test** — build a `ServiceCollection`, call `AddThemiaNotificationsModule(o => o.ConnectionStringName = "X")` with the EF peer flag, assert: `INotificationDispatcher`, `IOutboxStore`, `IPreferenceResolver`, `IProviderConfigResolver`, `DrainSignal`, the dialect, and a hosted `OutboxDrainer` are registered; options validated (blank conn string throws). Use `services.BuildServiceProvider()` and `GetRequiredService`/`GetServices<IHostedService>()`.

- [ ] **Step 2: Implement `NotificationsModule`** — mirrors `StorageModule`: ctor takes `MigrationEngine`, `InitializeAsync` reads `options.ConnectionStringName` and runs `ThemiaMigrations.Run(engine, conn, typeof(NotificationsSchemaMigration).Assembly)`.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Notifications.Migrations;

namespace Themia.Modules.Notifications;

/// <summary>The Themia Notifications module: outbox, drainer, dispatcher, preferences, in-app, provider config.</summary>
public sealed class NotificationsModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly NotificationsModuleOptions options;

    /// <summary>Creates the module with default options.</summary>
    public NotificationsModule(MigrationEngine engine) : this(engine, new NotificationsModuleOptions()) { }

    /// <summary>Creates the module with explicit options.</summary>
    public NotificationsModule(MigrationEngine engine, NotificationsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Notifications",
        displayName: "Notifications",
        description: "Tenant-aware notifications: transactional outbox, background drainer, multi-channel dispatcher.",
        version: new Version(0, 6, 3, 0));

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the notifications module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(NotificationsSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Implement `NotificationsServiceCollectionExtensions`** — the registration entry point. Takes the `MigrationEngine` (to pick the dialect) and the connection string name (the dialect needs the resolved string at build time → resolve lazily from `IConfiguration` in a factory). Registers the dialect singleton, `DrainSignal` singleton, the stores/dispatcher/resolvers scoped, and `AddHostedService<OutboxDrainer>()`. Mirrors Storage's `ContributeDapperMappings` scan. Choose EF vs Dapper store impls via a `NotificationsBuilder.UseEf()/UseDapper()` (default EF), matching how Storage selects a backend.

```csharp
public static class NotificationsServiceCollectionExtensions
{
    public static NotificationsBuilder AddThemiaNotificationsModule(
        this IServiceCollection services, MigrationEngine engine, Action<NotificationsModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new NotificationsModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        // Dialect: resolve the connection string lazily from IConfiguration at first use.
        services.TryAddSingleton<INotificationsSqlDialect>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var conn = cfg.GetConnectionString(options.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{options.ConnectionStringName}' was not found.");
            return engine switch
            {
                MigrationEngine.Postgres => new PostgresNotificationsDialect(conn),
                MigrationEngine.MySql => new MySqlNotificationsDialect(conn),
                MigrationEngine.SqlServer => new SqlServerNotificationsDialect(conn),
                _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unsupported engine."),
            };
        });

        services.TryAddSingleton<DrainSignal>();
        services.TryAddScoped<IPreferenceResolver, PreferenceResolver>();
        services.TryAddScoped<IProviderConfigResolver, ProviderConfigResolver>();
        services.TryAddScoped<INotificationDispatcher, NotificationDispatcher>();

        ContributeDapperMappings(services);
        services.AddHostedService<OutboxDrainer>();
        return new NotificationsBuilder(services);
    }
    // UseEf()/UseDapper() register the store + outbox impls; default to EF in the builder ctor.
}
```

> The EF stores need the adopter's `ThemiaDbContext`; the adopter must call `modelBuilder.ApplyThemiaNotifications()` in their context (documented). The Dapper stores need the framework Dapper services already registered. The drainer's `IEmailSender`/`ISmsSender`/`IPushSender` come from the neutral core's `AddThemiaNotifications(...)` — document that adopters call both.

- [ ] **Step 4: Run** — Expected: PASS.

- [ ] **Step 5: Finalize `PublicAPI.Unshipped.txt`** — `NotificationsModule`, `NotificationsServiceCollectionExtensions`, `NotificationsBuilder`. Clean build (`--no-incremental`) → no `RS0016`.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat: add NotificationsModule and AddThemiaNotificationsModule DI wiring"
```

---

### Task 15: Full-path integration test + per-tenant SMTP wiring

**Files:**
- Create: `tests/Themia.Modules.Notifications.IntegrationTests/DispatchEndToEndTests.cs`
- Modify (if needed): `Config`/sender wiring for per-tenant SMTP

- [ ] **Step 1: Write the end-to-end test** (Postgres at minimum; parametrize to 3 engines) — boot a host with `AddThemiaNotifications()` (core, fake `IEmailSender` recording sends) + `AddThemiaNotificationsModule(Postgres, ...)` + EF peer + a tenant context; run the module's `InitializeAsync` to migrate; `INotificationDispatcher.DispatchAsync` an Email request; `SaveChanges`; kick `DrainSignal`; start the host; await the fake recording the send; assert the outbox row is `sent`. Also assert tenant isolation: a row for tenant A is not visible when querying as tenant B through the stores.

- [ ] **Step 2: Wire per-tenant SMTP** — if the per-tenant credential path isn't exercised, add a thin `TenantAwareSmtpEmailSender` (in this module) that wraps the core SMTP send but pulls creds from `IProviderConfigResolver` (falling back to global `SmtpEmailOptions`). Register it via a `NotificationsBuilder.UsePerTenantSmtp()` opt-in. Only build this if the e2e test needs it; otherwise document the seam and defer (YAGNI — the resolver already exists for consumers to use).

- [ ] **Step 3: Run** — `dotnet test tests/Themia.Modules.Notifications.IntegrationTests` — Expected: all PASS (Docker required).

- [ ] **Step 4: Commit**

```bash
git commit -am "test: add end-to-end notifications dispatch integration tests"
```

---

### Task 16: Version bump, CHANGELOG, docs

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.6.2</Version>` → `0.6.3`)
- Modify: `CHANGELOG.md`
- Modify: `PublicAPI.Shipped.txt` ← move all `Unshipped` lines on release? (No — Shipped promotion happens at release tooling time; leave Unshipped curated. Confirm the repo's convention from a prior module's release PR before moving lines.)

- [ ] **Step 1: Bump the version**

```xml
<Version>0.6.3</Version>
```

- [ ] **Step 2: Add the CHANGELOG entry** under a new `## [0.6.3] - 2026-06-22` (and keep `## [Unreleased]` empty above it):

```markdown
## [0.6.3] - 2026-06-22

### Added
- `Themia.Modules.Notifications` — tenant-aware notifications module over the `Themia.Notifications`
  core. A transactional outbox (`IOutboxStore`, staged in the caller's unit of work), a
  near-real-time background drainer (`OutboxDrainer` + `DrainSignal`) with per-engine atomic claim
  (PostgreSQL/MySQL `FOR UPDATE SKIP LOCKED`, SQL Server `READPAST/UPDLOCK` + `OUTPUT`), lease-based
  reclaim of crashed drainers, and exponential backoff → dead-letter. An `INotificationDispatcher`
  routes events to channels via per-tenant/user `NotificationPreference` (external channels enqueue;
  in-app writes directly). In-app notification store, per-tenant `TenantProviderConfig` resolver,
  EF Core + Dapper store peers over one FluentMigrator schema (PostgreSQL + MySQL + SQL Server),
  and an `AddThemiaNotificationsModule` DI extension. Targets `net10.0`.
```

- [ ] **Step 3: Build + full test** — `dotnet build Themia.sln --no-incremental` (no warnings/errors), then `dotnet test Themia.sln --filter Category!=Integration` (unit suites green; the heavy Testcontainers suites are gated like the other modules' and run in CI).

- [ ] **Step 4: Commit**

```bash
git commit -am "chore: bump to 0.6.3 and document Themia.Modules.Notifications in CHANGELOG"
```

---

## Self-Review (run after the plan, before execution)

**Spec coverage** (each spec §In-scope item → task):
- Channel senders (Email/Sms/Push seam) → already shipped in the core (0.6.2); the module consumes them in the drainer (Task 10). ✅
- Transactional outbox + FluentMigrator schema (3 engines) → Tasks 3, 7. ✅
- Near-real-time drainer (signal + poll + lease + backoff + scheduled_for) → Tasks 8, 9, 10. ✅
- Per-engine atomic claim → Task 9. ✅
- `INotificationDispatcher` + preferences → Tasks 11, 12. ✅
- In-app written directly → Task 12. ✅
- Per-tenant provider config → Tasks 6 (store), 13 (resolver), 15 (SMTP wiring). ✅
- Tenant isolation on all entities (EF + Dapper peers) → Tasks 4, 5, 6, 15. ✅
- Versioning/CHANGELOG/coord → Task 16 (coord filed post-merge, outside the plan). ✅

**Open implementation decision the implementer must close (flagged in Task 12):** the in-app store's `AddAsync` must NOT call `SaveChanges` when used by the dispatcher (the dispatcher stages in the caller's UoW). Resolve by splitting stage-only vs save-and-commit APIs and keep the module consistent. This is the one place two tasks (6 and 12) interact — call it out to the implementer.

**Type consistency:** `OutboxMessage` fields, `OutboxStatus` integer values (0–4), `ClaimedOutboxRow`, `NotificationRequest`, `ResolvedPreferences`, `INotificationsSqlDialect` method signatures are used identically across Tasks 7–14. `NotificationChannel`/`NotificationMessage`/`NotificationResult`/`INotificationTemplateRenderer` come from the shipped core — verify their exact namespaces in Task 2/10 against `src/neutral/Themia.Notifications`.

**Placeholder scan:** no TBD/TODO; every code step has complete code. Repeated patterns (the 4 EF configs, the Dapper mappings, the 3 dialects, the 3 store pairs) are spelled out as "write all N completely" with one full exemplar each — acceptable given the mechanical symmetry, but the implementer writes every file in full (no "similar to above" in the actual code).

---

## Execution Handoff

Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task, two-stage review (spec compliance, then code quality) between tasks, fast iteration. Matches how 0.6.0–0.6.2 were built.

**2. Inline Execution** — execute tasks in this session via executing-plans, batch with checkpoints.

Which approach?
