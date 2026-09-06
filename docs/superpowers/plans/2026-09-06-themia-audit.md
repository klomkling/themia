# Themia.Audit Implementation Plan (0.23.0)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ship an append-only, tenant-aware audit event log — activity events the app records and
authentication events the framework records for it — making three already-shipped dead seams live.

**Architecture:** A framework-neutral core (`Themia.Audit`) owns the record model, validation, redaction,
a Dapper store behind a per-engine dialect strategy, and the FluentMigrator schema. A module
(`Themia.Modules.Audit`) adds tenant resolution, transaction enlistment, the `IAuditLogService` adapter,
and an observer that audits Identity. A separate ASP.NET package mounts a read-only dashboard.

**Tech Stack:** .NET 8 + .NET 10, Dapper, FluentMigrator, System.Text.Json, xUnit, Testcontainers
(PostgreSQL 16-alpine, MySQL 8.4, SQL Server 2022-CU14).

**Spec:** `docs/superpowers/specs/2026-09-06-themia-audit-design.md` — read it before Task 1. It carries
every "why"; this plan carries the "what". Where they disagree, the spec wins.

## Global Constraints

- **TFMs:** `Themia.Audit`, its three dialects, and `Themia.Audit.AspNetCore` → `net8.0;net10.0`.
  `Themia.Modules.Audit` → `net10.0`. Non-negotiable (`CLAUDE.md`).
- **`Themia.Audit` must not reference any `Themia.Framework.*` package.** Task 1 adds the test.
- `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`. A clean `--no-incremental` build must
  report **zero** warnings and no `RS0016`. Every new public member goes in `PublicAPI.Unshipped.txt`.
- **`System.Text.Json` only.** Never `Newtonsoft.Json`.
- **`ILogger<T>` only.** No `Console.*`.
- **Table name is `themia_audit_events`, unqualified, identical on all three engines. Never
  `InSchema(...)`.** See spec §5.
- **Every enum reserves `0` for `Unspecified`** and is validated with `Enum.IsDefined`.
- Central Package Management: versions live in `Directory.Packages.props`, never in a csproj.
- Version stays `0.22.1` until Task 9 bumps it to `0.23.0`.
- Commit after every task. Conventional commits, imperative mood, no co-author or "generated with" lines.

---

## File Structure

```
src/neutral/Themia.Audit/
  AuditCategory.cs  AuditOutcome.cs  AuditEngine.cs
  AuditEntry.cs                      # record + length constants
  AuditQuery.cs   PagedResult.cs
  IAuditRecorder.cs  AuditRecorder.cs
  IAuditStore.cs     AuditStoreEngine.cs
  IAuditDialect.cs
  Redaction/IAuditRedactor.cs  Redaction/AuditRedactor.cs  Redaction/AuditRedactionOptions.cs
  Http/AuditHttpEnricher.cs
  Migrations/AuditSchemaMigration.cs
  AuditOptions.cs
  DependencyInjection/AuditServiceCollectionExtensions.cs
src/neutral/Themia.Audit.PostgreSql|SqlServer|MySql/
  <Engine>AuditDialect.cs   DependencyInjection/ServiceCollectionExtensions.cs
src/neutral/Themia.Audit.AspNetCore/
  AuditDashboardOptions.cs  AuditDashboardEndpoints.cs  DashboardHtml.cs  DashboardCss.cs
src/framework/Themia.Framework.Data.Abstractions/Connections/IAmbientConnectionAccessor.cs
src/framework/Themia.Framework.Data.EFCore/Connections/EfAmbientConnectionAccessor.cs
src/framework/Themia.Framework.Data.Dapper/Connections/DapperAmbientConnectionAccessor.cs
src/modules/Themia.Modules.Identity.Abstractions/Authentication/IIdentityEventObserver.cs
src/modules/Themia.Modules.Audit/
  AuditTransactionPolicy.cs      TransactionalAuditRecorder.cs
  ITenantAuditReader.cs          TenantAuditReader.cs
  AuditLogServiceAdapter.cs      AuditingIdentityObserver.cs
  AuditModule.cs  AuditModuleOptions.cs
  DependencyInjection/AuditModuleServiceCollectionExtensions.cs
tests/Themia.Audit.Tests/                    # unit: model, redaction, enrichment, layering
tests/Themia.Audit.IntegrationTests/         # 3 engines: store, dialects AND schema — one shared
                                             # container per engine, migration class included
tests/Themia.Audit.AspNetCore.Tests/         # dashboard
tests/Themia.Modules.Audit.Tests/            # policy, tenant reader, observer, DI graph
```

---

## Task 1: Neutral model, validation, redaction

**Files:**
- Create: `src/neutral/Themia.Audit/Themia.Audit.csproj`, `AuditCategory.cs`, `AuditOutcome.cs`,
  `AuditEngine.cs`, `AuditEntry.cs`, `AuditQuery.cs`, `PagedResult.cs`,
  `Redaction/{IAuditRedactor,AuditRedactor,AuditRedactionOptions}.cs`
- Test: `tests/Themia.Audit.Tests/{AuditEntryValidationTests,AuditRedactorTests,LayeringTests}.cs`

**Interfaces:**
- Produces: `AuditEntry` (+ `MaxEventTypeLength = 100`, `MaxActorIdLength = 256`,
  `MaxActorNameLength = 256`, `MaxEntityTypeLength = 256`, `MaxEntityIdLength = 256`,
  `MaxReasonLength = 256`, `MaxCorrelationIdLength = 128`, `MaxIpAddressLength = 64`,
  `MaxUserAgentLength = 512`), `AuditCategory`, `AuditOutcome`, `AuditEngine`,
  `AuditQuery`, `PagedResult<T>`, `IAuditRedactor.Redact(string json) -> string`.

- [ ] **Step 1: Write the failing validation tests**

```csharp
[Fact]
public void Validate_rejects_over_length_event_type()
{
    var entry = new AuditEntry { EventType = new string('x', AuditEntry.MaxEventTypeLength + 1),
                                 Category = AuditCategory.Activity, Outcome = AuditOutcome.Success };
    var ex = Assert.Throws<ArgumentException>(() => entry.Validate());
    Assert.Contains(nameof(AuditEntry.EventType), ex.Message, StringComparison.Ordinal);
}

[Fact]
public void Validate_rejects_unspecified_category()
{
    var entry = Valid() with { Category = AuditCategory.Unspecified };
    Assert.Throws<ArgumentException>(() => entry.Validate());
}

[Fact]
public void Validate_truncates_user_agent_instead_of_rejecting()
{
    var entry = Valid() with { UserAgent = new string('u', AuditEntry.MaxUserAgentLength + 50) };
    var normalized = entry.Normalize();
    Assert.Equal(AuditEntry.MaxUserAgentLength, normalized.UserAgent!.Length);
}
```

Adopter-named fields reject; framework-captured ones truncate (spec §5).

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Themia.Audit.Tests --filter AuditEntryValidationTests`. Expected: does not compile / type not found.

- [ ] **Step 3: Implement `AuditEntry`**

```csharp
public sealed record AuditEntry
{
    public const int MaxEventTypeLength = 100;
    public const int MaxActorIdLength = 256;
    public const int MaxActorNameLength = 256;
    public const int MaxEntityTypeLength = 256;
    public const int MaxEntityIdLength = 256;
    public const int MaxReasonLength = 256;
    public const int MaxCorrelationIdLength = 128;
    public const int MaxIpAddressLength = 64;
    public const int MaxUserAgentLength = 512;

    public Guid EventUid { get; init; } = Guid.NewGuid();
    public string? TenantId { get; init; }
    public AuditCategory Category { get; init; }
    public required string EventType { get; init; }
    public AuditOutcome Outcome { get; init; }
    public string? ActorId { get; init; }
    public string? ActorName { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? CorrelationId { get; init; }
    public string? Reason { get; init; }

    /// <summary>Set by <see cref="AuditRecorder"/> from a payload object. Never assigned by a caller.</summary>
    public string? Data { get; internal init; }

    /// <summary>Throws when an adopter-named field exceeds its column, or an enum is unset/undefined.</summary>
    public void Validate() { /* per-field guards; Enum.IsDefined on Category and Outcome */ }

    /// <summary>Returns a copy with framework-captured fields clipped to their columns.</summary>
    public AuditEntry Normalize() => this with
    {
        UserAgent = Clip(UserAgent, MaxUserAgentLength),
        IpAddress = Clip(IpAddress, MaxIpAddressLength),
    };
}
```

`Data` is `internal init` so the "recorder serializes, callers never supply a string" invariant is
enforced by the compiler, not a doc comment (spec §8).

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Write the failing redaction tests**

```csharp
// Each case asserts against ITS OWN secret. Asserting all four on every case makes three of the
// four assertions vacuous per case — they check a substring the input never contained.
[Theory]
[InlineData("""{"password":"SEC-1"}""", "SEC-1")]
[InlineData("""{"outer":{"apiKey":"SEC-2"}}""", "SEC-2")]                  // nested object
[InlineData("""{"a":{"b":{"c":{"ssn":"SEC-3"}}}}""", "SEC-3")]            // deep
public void Redacts_a_secret_at_any_depth(string json, string secret)
{
    var result = new AuditRedactor(new AuditRedactionOptions()).Redact(json);
    Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
    // Presence matters as much as absence: a redactor that dropped the property entirely would
    // pass an absence-only test while losing the fact that a password field was there at all.
    Assert.Contains("[redacted]", result, StringComparison.Ordinal);
}

[Fact]
public void Redacts_every_element_of_an_array_not_only_the_first()
{
    var result = new AuditRedactor(new AuditRedactionOptions())
        .Redact("""{"items":[{"token":"SEC-A"},{"token":"SEC-B"},{"token":"SEC-C"}]}""");
    Assert.DoesNotContain("SEC-A", result, StringComparison.Ordinal);
    Assert.DoesNotContain("SEC-B", result, StringComparison.Ordinal);
    Assert.DoesNotContain("SEC-C", result, StringComparison.Ordinal);
}

[Fact]
public void Survives_shapes_the_recorder_can_hand_it()
{
    // The recorder serializes arbitrary payload objects, so all of these are reachable inputs.
    var r = new AuditRedactor(new AuditRedactionOptions());
    foreach (var json in new[] { """{"a":null}""", "{}", """[1,2,3]""", "\"bare\"", "42" })
        _ = r.Redact(json);   // must not throw
}

[Fact]
public void Keeps_the_property_name_so_its_presence_is_auditable()
    => Assert.Contains("password", new AuditRedactor(new AuditRedactionOptions())
        .Redact("""{"password":"hunter2"}"""), StringComparison.Ordinal);

[Fact]
public void Adopter_patterns_add_and_never_remove_defaults()
{
    var o = new AuditRedactionOptions();
    o.AddPattern("internal_ref");
    var r = new AuditRedactor(o).Redact("""{"internal_ref":"x","password":"y"}""");
    Assert.DoesNotContain("\"x\"", r, StringComparison.Ordinal);
    Assert.DoesNotContain("\"y\"", r, StringComparison.Ordinal);
}
```

Nested/array/deep cases are mandatory: a flat one-level test proves only the easy case (spec §14).

- [ ] **Step 6: Run to verify failure**

- [ ] **Step 7: Implement `AuditRedactor`** — walk with `JsonDocument`/`Utf8JsonWriter`, replacing a
      matched property's value with the string `[redacted]` regardless of its original kind. Default
      deny-list exactly as spec §8 lists it, matched with `OrdinalIgnoreCase`.

- [ ] **Step 8: Run — expect PASS**

- [ ] **Step 9: Add the layering assertion**

```csharp
[Fact]
public void Themia_Audit_references_no_framework_package()
{
    var referenced = typeof(AuditEntry).Assembly.GetReferencedAssemblies()
        .Select(a => a.Name!)
        .Where(n => n.StartsWith("Themia.Framework.", StringComparison.Ordinal))
        .ToArray();
    Assert.Empty(referenced);
}
```

- [ ] **Step 10: Run — expect PASS**

- [ ] **Step 11: Commit**

```bash
git add src/neutral/Themia.Audit tests/Themia.Audit.Tests
git commit -m "feat(audit): audit entry model, length validation and JSON redaction"
```

---

## Task 2: Store, dialects, schema migration

**Files:**
- Create: `src/neutral/Themia.Audit/{IAuditStore,AuditStoreEngine,IAuditDialect}.cs`,
  `Migrations/AuditSchemaMigration.cs`, and the three dialect projects.
- Test: `tests/Themia.Audit.Integration.Tests/`, `tests/Themia.Audit.Migration.Tests/`

**Interfaces:**
- Consumes: `AuditEntry`, `AuditQuery`, `PagedResult<T>` (Task 1).
- Produces: `IAuditStore` (`WriteAsync -> long`, `QueryAsync`, `GetAsync(Guid)`, `PurgeAsync`),
  `IAuditDialect` (`CreateConnection`, `InsertSql`, `SelectPageSql`, `CountSql`, `PurgeSql`).

**Container discipline: ONE container per engine, shared by every test class including the migration
class — three containers total, in a single integration test project.**

Copy `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceEngineFixtures.cs`, whose XML doc
states the reasoning at `:25-36`. Do not give the migration tests a separate container: `Up()` is guarded
by `Schema.Table(...).Exists()`, `ThemiaMigrations.Run` holds an exclusive advisory lock, and xUnit never
runs two classes from one collection concurrently. CI shares a four-core box (coord #0109).

- [ ] **Step 1: Write the failing migration test**

```csharp
[Fact]
public async Task Creates_the_same_unqualified_table_on_every_engine()
{
    ThemiaMigrations.Run(Engine, ConnectionString, typeof(AuditSchemaMigration).Assembly);
    await using var conn = Dialect.CreateConnection(ConnectionString);
    await conn.OpenAsync();
    // Unqualified on purpose: InSchema is dropped on MySQL and the name would then differ per engine.
    var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM themia_audit_events");
    Assert.Equal(0, count);
}

[Fact]
public async Task Up_is_replay_safe()
{
    // FluentMigrator's runner is synchronous; there is no RunAsync.
    ThemiaMigrations.Run(Engine, ConnectionString, typeof(AuditSchemaMigration).Assembly);
    ThemiaMigrations.Run(Engine, ConnectionString, typeof(AuditSchemaMigration).Assembly);
}
```

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Implement `AuditSchemaMigration`** — copy the structure of
      `ChallengeSchemaMigration`/`SequencesSchemaMigration` exactly:
      unsupported-provider guard **first**, then `if (Schema.Table("themia_audit_events").Exists()) return;`,
      then `IfDatabase("postgresql"|"mysql"|"sqlserver")` branches for the per-engine `occurred_at` type.
      `id` is `.AsInt64().PrimaryKey().Identity()`; `event_uid` is `.AsGuid().NotNullable()` with a unique
      index; `data` is `.AsString(int.MaxValue).Nullable()` on every engine. Four indexes per spec §5.

- [ ] **Step 4: Run — expect PASS on all three engines**

- [ ] **Step 5: Write the failing store tests**

```csharp
[Fact]
public async Task Every_field_lands_in_its_own_column()
{
    // Distinct values per field: five consecutive nullable strings make a transposition invisible,
    // which is the defect that shipped in 0.21.4's outbox dialects.
    var entry = new AuditEntry
    {
        EventType = "EVT", Category = AuditCategory.Activity, Outcome = AuditOutcome.Failure,
        TenantId = "tenant-A", ActorId = "actor-1", ActorName = "actor-name-2",
        EntityType = "entity-type-3", EntityId = "entity-id-4", Reason = "reason-5",
        CorrelationId = "corr-6", IpAddress = "10.0.0.1", UserAgent = "agent-7",
        OccurredAt = DateTimeOffset.UtcNow,
    };
    await Store.WriteAsync(entry, Conn, null, default);
    var read = await Store.GetAsync(entry.EventUid, Conn, default);
    Assert.Equal("actor-1", read!.ActorId);
    Assert.Equal("actor-name-2", read.ActorName);
    Assert.Equal("entity-type-3", read.EntityType);
    Assert.Equal("entity-id-4", read.EntityId);
    Assert.Equal("reason-5", read.Reason);
    Assert.Equal("corr-6", read.CorrelationId);
}

[Fact]
public async Task Null_tenant_round_trips_as_null_not_empty_string()
{
    var e = Valid() with { TenantId = null };
    await Store.WriteAsync(e, Conn, null, default);
    Assert.Null((await Store.GetAsync(e.EventUid, Conn, default))!.TenantId);
}

[Fact]
public async Task Write_returns_increasing_ids()
{
    var first = await Store.WriteAsync(Valid(), Conn, null, default);
    var second = await Store.WriteAsync(Valid(), Conn, null, default);
    Assert.True(second > first);
}

[Fact]
public async Task Paging_is_stable_when_rows_share_a_timestamp()
{
    var at = DateTimeOffset.UtcNow;
    for (var i = 0; i < 10; i++) await Store.WriteAsync(Valid() with { OccurredAt = at }, Conn, null, default);
    var p1 = await Store.QueryAsync(new AuditQuery { Page = 1, PageSize = 5 }, Conn, default);
    var p2 = await Store.QueryAsync(new AuditQuery { Page = 2, PageSize = 5 }, Conn, default);
    Assert.Empty(p1.Items.Select(x => x.EventUid).Intersect(p2.Items.Select(x => x.EventUid)));
}
```

- [ ] **Step 6: Run to verify failure**

- [ ] **Step 7: Implement `AuditStoreEngine` + the three dialects.** `SelectPageSql` orders by
      `occurred_at DESC, id DESC` (spec §6) with engine-appropriate paging (`LIMIT/OFFSET`,
      `OFFSET … FETCH NEXT`). `data` is bound as a plain string on every engine — **no `jsonb` cast**.

- [ ] **Step 8: Run — expect PASS on all three engines**

- [ ] **Step 9: Commit**

```bash
git add src/neutral/Themia.Audit src/neutral/Themia.Audit.* tests/Themia.Audit.Integration.Tests tests/Themia.Audit.Migration.Tests
git commit -m "feat(audit): append-only Dapper store, per-engine dialects and FluentMigrator schema"
```

---

## Task 3: `AuditRecorder`, HTTP enrichment, `AddThemiaAudit`

**Files:**
- Create: `src/neutral/Themia.Audit/{IAuditRecorder,AuditRecorder,AuditOptions}.cs`,
  `Http/AuditHttpEnricher.cs`, `DependencyInjection/AuditServiceCollectionExtensions.cs`
- Test: `tests/Themia.Audit.Tests/AuditRecorderTests.cs`,
  `tests/Themia.Audit.Integration.Tests/NeutralOnlyMigrationTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces: `IAuditRecorder.RecordAsync(AuditEntry, object? payload = null, CancellationToken) -> ValueTask<Guid>`,
  `AddThemiaAudit(Action<AuditOptions>, bool runMigration = true)`.

- [ ] **Step 1: Write the failing recorder tests**

```csharp
[Fact]
public async Task Serializes_the_payload_and_redacts_it()
{
    var store = new CapturingStore();
    var recorder = new AuditRecorder(store, new AuditRedactor(new AuditRedactionOptions()), TimeProvider.System);
    await recorder.RecordAsync(Valid(), new { userId = 7, password = "hunter2" }, default);
    Assert.Contains("\"userId\":7", store.Last!.Data, StringComparison.Ordinal);
    Assert.DoesNotContain("hunter2", store.Last.Data!, StringComparison.Ordinal);
}

[Fact]
public async Task Null_payload_leaves_data_null()
{
    // ...assert store.Last.Data is null — an empty "{}" would make every row look like it carried one.
}

[Fact]
public async Task Validates_before_writing()
{
    var store = new CapturingStore();
    var recorder = new AuditRecorder(store, Redactor, TimeProvider.System);
    await Assert.ThrowsAsync<ArgumentException>(() =>
        recorder.RecordAsync(Valid() with { Category = AuditCategory.Unspecified }, null, default).AsTask());
    Assert.Null(store.Last);   // nothing reached the store
}
```

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Implement `AuditRecorder`** — `Validate()` → `Normalize()` → serialize payload with
      `JsonSerializer` → `IAuditRedactor.Redact` → `IAuditStore.WriteAsync`. Return `EventUid`.
      Stamp `OccurredAt` from an injected `TimeProvider` when the caller left it default.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Write the failing neutral-only migration test**

```csharp
[Fact]
public async Task AddThemiaAudit_creates_the_table_with_no_module_registered()
{
    var services = new ServiceCollection();
    services.AddThemiaAudit(o => { o.ConnectionString = Cs; o.Engine = AuditEngine.Postgres; });
    services.AddThemiaAuditPostgreSql();
    await using var sp = services.BuildServiceProvider();
    _ = sp.GetRequiredService<IAuditRecorder>();
    await using var conn = new NpgsqlConnection(Cs);
    await conn.OpenAsync();
    Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM themia_audit_events"));
}

[Fact]
public void AddThemiaAudit_rejects_an_unset_engine()
{
    var services = new ServiceCollection();
    services.AddThemiaAudit(o => { o.ConnectionString = Cs; /* Engine left Unspecified */ });
    Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider()
        .GetRequiredService<IOptions<AuditOptions>>().Value);
}
```

- [ ] **Step 6: Run to verify failure**

- [ ] **Step 7: Implement `AddThemiaAudit`** — register options with `ValidateOnStart`, the redactor,
      `AuditRecorder` as `IAuditRecorder`, the store, and the HTTP enricher; when `runMigration` is true
      call `ThemiaMigrations.Run(engine, connectionString, typeof(AuditSchemaMigration).Assembly)`,
      mirroring `Themia.Exceptional`'s `ServiceCollectionExtensions`.

- [ ] **Step 8: Run — expect PASS**

- [ ] **Step 9: Write the failing enrichment tests**

```csharp
[Fact]
public void Reads_the_caller_address_and_user_agent_from_the_current_request()
{
    var ctx = new DefaultHttpContext();
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
    ctx.Request.Headers.UserAgent = "test-agent/1.0";
    var enriched = new AuditHttpEnricher(Accessor(ctx)).Enrich(Valid());
    Assert.Equal("203.0.113.7", enriched.IpAddress);
    Assert.Equal("test-agent/1.0", enriched.UserAgent);
}

[Fact]
public void Records_nulls_outside_a_request_rather_than_throwing()
{
    // A Quartz worker host has no HttpContext. Nulls are the correct answer, not an error (spec §10f).
    var enriched = new AuditHttpEnricher(Accessor(null)).Enrich(Valid());
    Assert.Null(enriched.IpAddress);
    Assert.Null(enriched.UserAgent);
}

[Fact]
public void Does_not_overwrite_values_the_caller_already_supplied()
    => Assert.Equal("10.1.1.1",
        new AuditHttpEnricher(Accessor(WithIp("203.0.113.7"))).Enrich(Valid() with { IpAddress = "10.1.1.1" }).IpAddress);
```

- [ ] **Step 10: Run to verify failure**

- [ ] **Step 11: Implement `AuditHttpEnricher`** over `IHttpContextAccessor`, following
      `Themia.Exceptional`'s `Serilog/HttpContextEnricher.cs`. A null `HttpContext` yields nulls.

- [ ] **Step 12: Run — expect PASS**

- [ ] **Step 13: Commit**

```bash
git commit -am "feat(audit): recorder with payload serialization, HTTP enrichment and AddThemiaAudit"
```

---

## Task 4: Dashboard — `Themia.Audit.AspNetCore`

**Files:**
- Create: `src/neutral/Themia.Audit.AspNetCore/{AuditDashboardOptions,AuditDashboardEndpoints,DashboardHtml,DashboardCss}.cs`
- Test: `tests/Themia.Audit.AspNetCore.Tests/AuditDashboardTests.cs`

**Interfaces:**
- Consumes: `IAuditStore`, `IAuditDialect`, `AuditQuery` (Tasks 1-3).
- Produces: `MapThemiaAuditDashboard(this IEndpointRouteBuilder, string path, Action<AuditDashboardOptions>)`.

Model on `Themia.Exceptional.AspNetCore` member for member. Copy its option semantics and its XML-doc
warnings verbatim where they carry (`HeadHtml` is not encoded; URLs are not re-based).

- [ ] **Step 1: Write the failing security tests**

```csharp
[Fact]
public async Task Null_authorize_denies_the_list()
    => Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/audit")).StatusCode);

[Fact]
public async Task Null_authorize_denies_the_detail_route_for_a_row_that_exists()
    => Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/audit/{ExistingUid}")).StatusCode);

[Fact]
public async Task A_throwing_authorize_fails_closed_not_open()
{
    // Authorize = _ => throw new InvalidOperationException()
    var res = await Client.GetAsync("/audit");
    Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    Assert.DoesNotContain("themia", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Implement the endpoints** — fail-closed `Authorize`, optional `OnDenied` that owns the
      response and still 404s if it throws, list + detail only, detail keyed on `event_uid`.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Write the failing disclosure and paging tests**

```csharp
[Fact]
public async Task ShowData_false_keeps_the_payload_off_the_page()
{
    // A row whose data contains "SENTINEL-PAYLOAD"; options.ShowData = false (the default)
    Assert.DoesNotContain("SENTINEL-PAYLOAD", await Client.GetStringAsync($"/audit/{Uid}"), StringComparison.Ordinal);
}

[Fact]
public async Task Page_size_is_clamped_to_the_maximum()
{
    var html = await Client.GetStringAsync("/audit?pageSize=100000");
    Assert.Equal(Options.MaxPageSize, CountRows(html));
}

[Fact]
public async Task Detail_does_not_resolve_a_sequential_id()
    => Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/audit/1")).StatusCode);
```

`ShowData` defaults to `false` — the opposite of Exceptional's `ShowRequestBody` — because redaction
filters by field *name* and a secret in an unnamed field survives to the page (spec §13).

- [ ] **Step 6: Run to verify failure** — [ ] **Step 7: Implement rendering** — [ ] **Step 8: Run — expect PASS**

- [ ] **Step 9: Commit**

```bash
git commit -am "feat(audit): mountable read-only audit dashboard with fail-closed authorization"
```

---

## Task 5: `IAmbientConnectionAccessor`

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Connections/IAmbientConnectionAccessor.cs`,
  `src/framework/Themia.Framework.Data.EFCore/Connections/EfAmbientConnectionAccessor.cs`,
  `src/framework/Themia.Framework.Data.Dapper/Connections/DapperAmbientConnectionAccessor.cs`
- Modify: both data layers' DI extensions to register their implementation.
- Test: `tests/Themia.Framework.Data.EFCore.Tests/`, `tests/Themia.Framework.Data.Dapper.Tests/`

**Interfaces:**
- Produces:
```csharp
public interface IAmbientConnectionAccessor
{
    /// <summary>The open connection and the transaction on it, or null when no transaction is open.</summary>
    Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken ct);
}
```

The transaction is non-nullable inside the tuple on purpose: returning a connection with no transaction
would let a caller believe it had enlisted when it had not.

**Why it lives here:** `RawConnectionBypassAnalyzer` (THEMIA103) flags
`IDapperConnectionContext.GetOpenConnectionAsync`, and `DataLayerScope.cs:14` exempts only assemblies
named `Themia.Framework.Data.*`. Implementing this inside the data layer keeps the audit module clear of
raw connections with no analyzer exception for anyone.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Returns_null_when_no_transaction_is_open()
    => Assert.Null(await Accessor.TryGetAsync(default));

[Fact]
public async Task Returns_the_ambient_connection_and_transaction_inside_ExecuteInTransactionAsync()
{
    (DbConnection, DbTransaction)? seen = null;
    await UnitOfWork.ExecuteInTransactionAsync(async ct => { seen = await Accessor.TryGetAsync(ct); });
    Assert.NotNull(seen);
    Assert.Equal(ConnectionState.Open, seen!.Value.Item1.State);
}
```

- [ ] **Step 2: Run to verify failure**
- [ ] **Step 3: Implement both** — EF: `Database.GetDbConnection()` + `Database.CurrentTransaction?.GetDbTransaction()`.
      Dapper: `IDapperConnectionContext.CurrentTransaction` and, only when it is non-null,
      `GetOpenConnectionAsync`.
- [ ] **Step 4: Run — expect PASS**
- [ ] **Step 5: Commit**

```bash
git commit -am "feat(data): IAmbientConnectionAccessor for EF Core and Dapper unit of work"
```

---

## Task 6: `IIdentityEventObserver` and every Identity call site

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/IIdentityEventObserver.cs`
- Modify: `Themia.Modules.Identity.AspNetCore/Authentication/AuthenticationFlow.cs`,
  `Themia.Modules.Identity.ExternalAuth.AspNetCore/External/ExternalAuthenticationFlow.cs`,
  `Themia.Modules.Identity/Services/UserService.cs`,
  `Themia.Modules.Identity.Abstractions/Authentication/AuthenticationHooks.cs` (add `LogoutContext.UserId`)
- Test: `tests/Themia.Modules.Identity.Tests/IdentityEventObserverTests.cs`

**Interfaces:**
- Produces: `IIdentityEventObserver` with the full method list from spec §10 — password login (3),
  external login (3), refresh (3), logout, lockout, user mutation. Every method has a default no-op body.

**Why a new interface rather than reusing the hooks:** `IAuthenticationHooks` and
`IExternalAuthenticationHooks` are `TryAddScoped` single registrations that can `Deny()`. An audit
consumer registering into either one either never fires or silently replaces the adopter's. Policy stays
single-owner; observation fans out. See spec §10a.

- [ ] **Step 1: Write the failing coverage tests** — one per event, asserting a recording observer sees it:

```csharp
public static TheoryData<Func<TestHost, Task>, string> EveryEvent => new()
{
    { h => h.LoginAsync("u", "good"),          nameof(IIdentityEventObserver.OnLoginSucceededAsync) },
    { h => h.LoginAsync("u", "bad"),           nameof(IIdentityEventObserver.OnLoginFailedAsync) },
    { h => h.LoginDeniedByHookAsync(),         nameof(IIdentityEventObserver.OnLoginDeniedAsync) },
    { h => h.ExternalLoginAsync(ok: true),     nameof(IIdentityEventObserver.OnExternalLoginSucceededAsync) },
    { h => h.ExternalLoginAsync(ok: false),    nameof(IIdentityEventObserver.OnExternalLoginFailedAsync) },
    { h => h.ExternalLoginDeniedAsync(),       nameof(IIdentityEventObserver.OnExternalLoginDeniedAsync) },
    { h => h.RefreshAsync(valid: true),        nameof(IIdentityEventObserver.OnRefreshSucceededAsync) },
    { h => h.RefreshReuseAsync(),              nameof(IIdentityEventObserver.OnRefreshFailedAsync) },
    { h => h.RefreshInvalidAsync(),            nameof(IIdentityEventObserver.OnRefreshFailedAsync) },
    { h => h.RefreshInactiveAccountAsync(),    nameof(IIdentityEventObserver.OnRefreshFailedAsync) },
    { h => h.LogoutAsync(),                    nameof(IIdentityEventObserver.OnLogoutAsync) },
    { h => h.LockoutAsync(),                   nameof(IIdentityEventObserver.OnLockedOutAsync) },
    { h => h.SetPasswordAsync(),               nameof(IIdentityEventObserver.OnUserMutatedAsync) },
};

[Theory, MemberData(nameof(EveryEvent))]
public async Task Every_identity_path_raises_its_event(Func<TestHost, Task> act, string expected)
{
    var observer = new RecordingObserver();
    var host = TestHost.With(observer);
    await act(host);
    Assert.Contains(expected, observer.Calls);
}
```

```csharp
[Fact]
public async Task Refresh_denied_reports_whether_the_rotation_had_committed()
{
    // Pre-rotation deny: rotationCommitted == false.
    // Post-rotation deny: rotationCommitted == true — the successor token exists and the client never
    // got it, which calls for a different response than "nothing happened".
}

[Fact]
public async Task Observers_and_the_adopters_own_hooks_both_run()
{
    // Register a custom IAuthenticationHooks AND an observer; assert both were invoked.
    // Same test for IExternalAuthenticationHooks.
}

[Fact]
public async Task A_throwing_observer_does_not_change_the_login_result()
{
    var withObserver = await LoginAsync(new ThrowingObserver());
    var without      = await LoginAsync();
    Assert.Equal(without.StatusCode, withObserver.StatusCode);
    Assert.Equal(await without.Content.ReadAsStringAsync(), await withObserver.Content.ReadAsStringAsync());
}
```

The last one matters twice over: a failed audit write must not turn a successful login into a 500, and
must not turn a *failed* login into a different status code, which would leak the reason the uniform 401
hides.

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Add `IIdentityEventObserver`** with default no-op bodies on every method.

- [ ] **Step 4: Invoke it at every site.** `AuthenticationFlow`: after `OnLoginSucceeded`/`OnLoginFailed`,
      at both refresh denial points (carrying `rotationCommitted`), at `ReuseDetected`, `Invalid` and the
      inactive/locked-out return, and in `LogoutAsync`. `ExternalAuthenticationFlow`: at its three hook
      points, carrying `wasCreated`/`wasLinked`. `UserService`: where lockout is applied
      (`UserService.cs:371-375`) and after each mutation. Each invocation is wrapped so a throwing
      observer is logged at Error and swallowed.

- [ ] **Step 5: Add `LogoutContext.UserId`** (nullable) and resolve the user from the refresh token
      before revocation so `OnLogoutAsync` can carry it.

- [ ] **Step 6: Run — expect PASS**

- [ ] **Step 7: Commit**

```bash
git commit -am "feat(identity): IIdentityEventObserver fan-out seam across every authentication path"
```

---

## Task 7: `Themia.Modules.Audit` — policy, tenant reader, adapter

**Files:**
- Create: `src/modules/Themia.Modules.Audit/{TransactionalAuditRecorder,ITenantAuditReader,TenantAuditReader,AuditLogServiceAdapter,AuditModule,AuditModuleOptions}.cs`,
  `DependencyInjection/AuditModuleServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Audit.Tests/`

**Interfaces:**
- Consumes: `IAuditRecorder`, `IAuditStore` (Tasks 1-3), `IAmbientConnectionAccessor` (Task 5),
  `IAuditLogService`/`AuditEvent` (`Themia.Framework.Services`), `ITenantContext`.
- Produces: `AddThemiaAuditModule(...)`, `ITenantAuditReader`, `AuditTransactionPolicy`.

- [ ] **Step 1: Write the failing policy tests**

```csharp
[Fact]
public async Task RequireTransaction_throws_and_the_message_names_the_fix()
{
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        Recorder.RecordAsync(Activity(), null, default).AsTask());
    Assert.Contains("ExecuteInTransactionAsync", ex.Message, StringComparison.Ordinal);
}

[Theory]
[InlineData(DataLayer.EfCore)]
[InlineData(DataLayer.Dapper)]
public async Task Activity_rows_roll_back_with_the_business_write(DataLayer layer)
{
    var host = Host(layer);
    await Assert.ThrowsAnyAsync<Exception>(() => host.UnitOfWork.ExecuteInTransactionAsync(async ct =>
    {
        await host.Recorder.RecordAsync(Activity(), null, ct);
        throw new InvalidOperationException("business failure");
    }));
    Assert.Equal(0, await host.CountAuditRowsAsync());
}

[Theory]
[InlineData(DataLayer.EfCore)]
[InlineData(DataLayer.Dapper)]
public async Task Activity_rows_commit_with_the_business_write(DataLayer layer) { /* mirror, assert 1 */ }

[Fact]
public async Task JoinIfPresent_writes_durably_with_no_transaction_and_does_not_throw() { }

[Fact]
public async Task Authentication_rows_survive_a_surrounding_rollback()
{
    // Category.Authentication is hardwired to Never: a login failure must be recorded even when the
    // request that contained it rolls back.
}
```

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Implement `TransactionalAuditRecorder`** — resolve the policy from the entry's
      `Category` (`Activity` from options, everything else `Never`), call `IAmbientConnectionAccessor`,
      throw / join / open accordingly, then delegate to the inner `AuditRecorder`. Resolve `TenantId`
      from `ITenantContext` when the entry left it null; never invent one.

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Write the failing tenant-read and adapter tests**

```csharp
[Fact]
public async Task TenantAuditReader_returns_only_the_ambient_tenants_rows()
{
    // Seed: 2 rows tenant-A, 3 rows tenant-B, 1 host-level. Ambient tenant = A.
    Assert.All((await Reader.QueryAsync(new AuditQuery(), default)).Items, e => Assert.Equal("tenant-A", e.TenantId));
}

[Fact]
public async Task TenantAuditReader_ignores_a_query_naming_another_tenant()
    => Assert.All((await Reader.QueryAsync(new AuditQuery { TenantId = "tenant-B" }, default)).Items,
                  e => Assert.Equal("tenant-A", e.TenantId));

[Fact]
public async Task Raw_store_query_crosses_tenants_by_design()
{
    // Documents the fail-open neutral surface rather than leaving it to be discovered (spec §9).
    Assert.Equal(6, (await Store.QueryAsync(new AuditQuery(), Conn, default)).Total);
}

[Fact]
public async Task IAuditLogService_resolves_from_the_real_DI_graph()
{
    // Built by AddThemiaAudit + AddThemiaAuditModule, not a hand-assembled container.
    // 0.19.0's notification-provider defect passed unit tests and failed on the real default graph.
    await using var sp = BuildRealHost().Services.CreateAsyncScope();
    var svc = sp.ServiceProvider.GetRequiredService<IAuditLogService>();
    await svc.WriteAsync(new AuditEvent("EVT", "a", "n", "e", "T", DateTimeOffset.UtcNow), default);
}
```

- [ ] **Step 6: Run to verify failure**
- [ ] **Step 7: Implement `TenantAuditReader`, `AuditLogServiceAdapter`, `AuditModule`,
      `AddThemiaAuditModule`.** The adapter maps `AuditEvent` onto `AuditEntry` per spec §12 and passes
      `Metadata` through as the payload object. `AuditModule.InitializeAsync` asserts the table exists
      and does **not** run the migration.
- [ ] **Step 8: Run — expect PASS**
- [ ] **Step 9: Commit**

```bash
git commit -am "feat(audit): audit module with transaction policy, tenant-scoped reader and IAuditLogService"
```

---

## Task 8: `AuditingIdentityObserver`

**Files:**
- Create: `src/modules/Themia.Modules.Audit/AuditingIdentityObserver.cs`
- Modify: `DependencyInjection/AuditModuleServiceCollectionExtensions.cs` (add `AddThemiaAuditIdentityObserver`)
- Test: `tests/Themia.Modules.Audit.Tests/AuditingIdentityObserverTests.cs`

- [ ] **Step 1: Write the failing mapping tests** — one per `IIdentityEventObserver` method, asserting the
      `EventType`, `Category`, `Outcome`, `ActorId` and `Reason` written:

```csharp
[Fact]
public async Task Login_failure_records_the_internal_reason_that_the_client_never_sees()
{
    await Observer.OnLoginFailedAsync("someone", LoginFailureReason.WrongPassword, default);
    Assert.Equal("LOGIN_FAILED", Store.Last!.EventType);
    Assert.Equal(AuditCategory.Authentication, Store.Last.Category);
    Assert.Equal(AuditOutcome.Failure, Store.Last.Outcome);
    Assert.Equal(nameof(LoginFailureReason.WrongPassword), Store.Last.Reason);
}

[Fact]
public async Task External_login_records_account_creation_and_linking()
{
    await Observer.OnExternalLoginSucceededAsync(UserId, "google", wasCreated: true, wasLinked: false, default);
    Assert.Contains("\"wasCreated\":true", Store.Last!.Data!, StringComparison.Ordinal);
}

[Fact]
public async Task Refresh_token_reuse_is_recorded_as_a_failure_with_its_outcome()
{
    await Observer.OnRefreshFailedAsync(UserId, RefreshOutcome.ReuseDetected, default);
    Assert.Equal(nameof(RefreshOutcome.ReuseDetected), Store.Last!.Reason);
}
```

- [ ] **Step 2: Run to verify failure**
- [ ] **Step 3: Implement the observer** — every method maps to one `AuditEntry` with
      `Category = Authentication` (or `UserLifecycle` for `OnUserMutatedAsync`), IP and user-agent from
      the enricher, tenant from `ITenantContext` when present.
- [ ] **Step 4: Run — expect PASS**
- [ ] **Step 5: Add `AddThemiaAuditIdentityObserver`**, registered with `AddScoped` into the
      `IEnumerable<IIdentityEventObserver>` fan-out — never `TryAdd`, which would drop it behind another
      observer.
- [ ] **Step 6: Run the full module suite — expect PASS**
- [ ] **Step 7: Commit**

```bash
git commit -am "feat(audit): audit every Identity authentication and user-lifecycle event"
```

---

## Task 9: Release — docs, version, PublicAPI

**Files:**
- Create: `src/neutral/Themia.Audit/README.md`, `src/modules/Themia.Modules.Audit/README.md`
- Modify: `Directory.Build.props` (`0.22.1` → `0.23.0`), `CHANGELOG.md`, `Themia.sln`,
  `docs/themia-architecture-overview.md` (module table + specs index), every touched
  `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add all six projects to `Themia.sln`** and confirm `dotnet build Themia.sln --no-incremental`
      is clean — zero warnings, no `RS0016`.
- [ ] **Step 2: Fill every `PublicAPI.Unshipped.txt`** for `Themia.Audit`, the three dialects,
      `Themia.Audit.AspNetCore`, `Themia.Modules.Audit`, `Themia.Modules.Identity.Abstractions`,
      `Themia.Framework.Data.{Abstractions,EFCore,Dapper}`.
- [ ] **Step 3: Write the two READMEs.** State plainly that `IAuditRecorder` is the real write surface and
      `IAuditLogService` is the compatibility adapter, and that `IAuditStore.QueryAsync` crosses tenants
      while `ITenantAuditReader` does not.
- [ ] **Step 4: Bump the version to `0.23.0`** in `Directory.Build.props`.
- [ ] **Step 5: Add the `CHANGELOG.md` entry** under a new `## [0.23.0]` heading — **Added** for the six
      packages and `IIdentityEventObserver`; note that `IAuditLogService`, `IAuthenticationHooks` and
      `IUserLifecycleHooks.OnUserMutatedAsync` now have a shipped consumer.
- [ ] **Step 6: Update `docs/themia-architecture-overview.md`** — mark the `Themia.Modules.Audit` row
      built, add the spec to the index, and record that the row's stated sources described an activity
      log rather than the change log the name implies (spec §1).
- [ ] **Step 7: Run the whole suite** — `dotnet test Themia.sln`. Every test, both TFMs, all three
      engines. Report the actual counts and durations; a pass claim that does not fit its own runtime is
      not a pass.
- [ ] **Step 8: Commit**

```bash
git commit -am "chore(audit): release 0.23.0 — Themia.Audit, dialects, dashboard and audit module"
```

---

## Deferred to release 2 (`0.24.0`)

Entity change log — `themia_audit_changes` keyed on `themia_audit_events.id`, opt-in per entity, and the
unresolved EF/Dapper parity fork. Spec §15. **Do not start it inside this plan.**
