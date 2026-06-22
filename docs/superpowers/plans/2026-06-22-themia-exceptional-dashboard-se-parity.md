# Themia.Exceptional Dashboard SE-Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the `Themia.Exceptional` dashboard to StackExchange.Exceptional parity — capture request context (headers/cookies/query/form/server-vars, redacted) and render a polished list + detail (formatted stack trace, request-context sections, relative time, protect/delete actions).

**Architecture:** Additive change across two packages over the single monorepo version (0.6.0 → 0.6.1). `Themia.Exceptional` gains one nullable `RequestContext` JSON column (new forward-only FluentMigrator migration), an opt-in `CaptureRequestContext` flag + configurable `Redactor` on `ExceptionalOptions`, enricher capture, and sink mapping. `Themia.Exceptional.AspNetCore` keeps hand-rendered HTML (no Razor/Handlebars dep) + one embedded CSS, re-skins list/detail, and adds POST protect/delete with a self-contained double-submit CSRF token.

**Tech Stack:** .NET 8 + .NET 10, Dapper, FluentMigrator (`IfDatabase` per engine), Serilog (sink + enricher), minimal-API endpoints, `System.Text.Json`, xUnit + `WebApplicationFactory` + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-06-22-themia-exceptional-dashboard-se-parity-design.md`

---

## File Structure

**`Themia.Exceptional` (capture/store core):**
- `ExceptionEntry.cs` — add `RequestContext` property.
- `ExceptionStoreParameters.cs` — add `RequestContext` to the INSERT param list.
- `ExceptionalOptions.cs` — add `CaptureRequestContext`, `Redactor`, `DefaultRedactor`.
- `Serilog/HttpContextEnricher.cs` — capture + redact request context → `"RequestContext"` property.
- `Serilog/ExceptionalSerilogSink.cs` — map the `"RequestContext"` property → `entry.RequestContext`.
- `Migrations/AddRequestContextColumn.cs` — **new** forward-only migration (nullable column, 3 engines).

**Dialect packages (SQL only):**
- `Themia.Exceptional.PostgreSql/PostgresExceptionalDialect.cs` — `InsertSql` gains the column + `@RequestContext`.
- `Themia.Exceptional.SqlServer/SqlServerExceptionalDialect.cs` — same.
- `Themia.Exceptional.MySql/MySqlExceptionalDialect.cs` — same.

**`Themia.Exceptional.AspNetCore` (dashboard):**
- `DashboardCss.cs` — **new** embedded CSS string (or `.css` EmbeddedResource).
- `DashboardHtml.cs` — re-skin list + detail; parse `Detail`/`RequestContext` JSON; CSRF hidden field.
- `ExceptionalDashboardOptions.cs` — add `EnableActions`, `ShowRequestContext`.
- `ExceptionalDashboardEndpoints.cs` — CSS route; POST protect/delete/hard-delete; double-submit CSRF.

**Tests:**
- `tests/Themia.Exceptional.Tests` — options/redactor/enricher/sink unit tests.
- `tests/Themia.Exceptional.IntegrationTests` — migration + round-trip across 3 engines (Testcontainers).
- `tests/Themia.Exceptional.AspNetCore.Tests` — dashboard render, XSS, CSRF, actions.

**Repo-wide:**
- `Directory.Build.props` — `<Version>` `0.6.0 → 0.6.1`.
- `CHANGELOG.md` — Added/Changed/Security entry.

---

### Task 1: Add `RequestContext` to the entity + write path

**Files:**
- Modify: `src/neutral/Themia.Exceptional/ExceptionEntry.cs`
- Modify: `src/neutral/Themia.Exceptional/ExceptionStoreParameters.cs:40`
- Modify: `src/neutral/Themia.Exceptional.PostgreSql/PostgresExceptionalDialect.cs:39-43`
- Modify: `src/neutral/Themia.Exceptional.SqlServer/SqlServerExceptionalDialect.cs` (InsertSql)
- Modify: `src/neutral/Themia.Exceptional.MySql/MySqlExceptionalDialect.cs` (InsertSql)
- Modify: `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Exceptional.Tests/ExceptionStoreParametersTests.cs`

These land together so the write path stays consistent (param + every dialect's SQL reference it). `GetByGuidSql`/`ListSql` are `SELECT *`, so they return the new column automatically once it exists — no change needed there.

- [ ] **Step 1: Write the failing test**

Create/append `tests/Themia.Exceptional.Tests/ExceptionStoreParametersTests.cs`:

```csharp
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class ExceptionStoreParametersTests
{
    [Fact]
    public void Insert_IncludesRequestContext()
    {
        var entry = new ExceptionEntry { RequestContext = "{\"headers\":{}}" };

        var p = ExceptionStoreParameters.Insert(entry, temporalDbType: null);

        Assert.Contains("RequestContext", p.ParameterNames);
        Assert.Equal("{\"headers\":{}}", p.Get<string?>("RequestContext"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter Insert_IncludesRequestContext`
Expected: FAIL — `ExceptionEntry` has no `RequestContext`, and the param is absent.

- [ ] **Step 3: Add the entity property**

In `src/neutral/Themia.Exceptional/ExceptionEntry.cs`, after the `RequestBody` property (line 67):

```csharp
    /// <summary>Captured request context (headers/cookies/query/form/server variables) as a JSON
    /// document, when <see cref="ExceptionalOptions.CaptureRequestContext"/> is enabled. Redacted at
    /// capture time per <see cref="ExceptionalOptions.Redactor"/>.</summary>
    public string? RequestContext { get; set; }
```

- [ ] **Step 4: Add the INSERT parameter**

In `src/neutral/Themia.Exceptional/ExceptionStoreParameters.cs`, in `Insert(...)` after the `RequestBody` line (line 40):

```csharp
        p.Add("RequestContext", entry.RequestContext);
```

- [ ] **Step 5: Add the column to all three dialect `InsertSql` statements**

In `PostgresExceptionalDialect.cs`, replace `InsertSql` (lines 39-43) so the column list ends with `,"RequestContext"` and the values list ends with `,@RequestContext`:

```csharp
    public string InsertSql => """
        INSERT INTO "Exceptions"
        ("Guid","ApplicationName","MachineName","Type","Source","Message","Detail","Host","Url","HttpMethod","IpAddress","StatusCode","ErrorHash","DuplicateCount","TenantId","CreationDate","LastLogDate","DeletionDate","IsProtected","RequestBody","RequestContext")
        VALUES (@Guid,@ApplicationName,@MachineName,@Type,@Source,@Message,@Detail,@Host,@Url,@HttpMethod,@IpAddress,@StatusCode,@ErrorHash,@DuplicateCount,@TenantId,@CreationDate,@LastLogDate,@DeletionDate,@IsProtected,@RequestBody,@RequestContext);
        """;
```

In `SqlServerExceptionalDialect.cs`, make the same edit using its bracket-quoted identifiers (`[RequestContext]` in the column list, `@RequestContext` in VALUES). In `MySqlExceptionalDialect.cs`, the same with its backtick/identifier style (`` `RequestContext` `` per that file's convention) and `@RequestContext`. Match each file's existing quoting exactly.

- [ ] **Step 6: Record the new public member**

In `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt` add:

```
Themia.Exceptional.ExceptionEntry.RequestContext.get -> string?
Themia.Exceptional.ExceptionEntry.RequestContext.set -> void
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter Insert_IncludesRequestContext`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Exceptional src/neutral/Themia.Exceptional.PostgreSql src/neutral/Themia.Exceptional.SqlServer src/neutral/Themia.Exceptional.MySql tests/Themia.Exceptional.Tests/ExceptionStoreParametersTests.cs
git commit -m "feat: add RequestContext column to the exception write path"
```

---

### Task 2: `CaptureRequestContext` option + `Redactor` + `DefaultRedactor`

**Files:**
- Modify: `src/neutral/Themia.Exceptional/ExceptionalOptions.cs`
- Modify: `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Exceptional.Tests/RedactorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.Tests/RedactorTests.cs`:

```csharp
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("Authorization", "Bearer abc")]
    [InlineData("Cookie", "session=xyz")]
    [InlineData("Set-Cookie", "session=xyz")]
    [InlineData("password", "hunter2")]
    [InlineData("ApiKey", "k-123")]
    [InlineData("X-Session-Token", "t-9")]
    public void DefaultRedactor_MasksSecretKeys(string key, string value)
        => Assert.Equal("***", ExceptionalOptions.DefaultRedactor(key, value));

    [Theory]
    [InlineData("User-Agent", "Edge")]
    [InlineData("email", "x@y.com")]
    [InlineData("id", "42")]
    public void DefaultRedactor_KeepsNonSecretValues(string key, string value)
        => Assert.Equal(value, ExceptionalOptions.DefaultRedactor(key, value));

    [Fact]
    public void Defaults_CaptureOffAndDefaultRedactor()
    {
        var o = new ExceptionalOptions();
        Assert.False(o.CaptureRequestContext);
        Assert.NotNull(o.Redactor);
        Assert.Equal("***", o.Redactor!("Authorization", "Bearer x"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter RedactorTests`
Expected: FAIL — members don't exist.

- [ ] **Step 3: Add the members**

In `src/neutral/Themia.Exceptional/ExceptionalOptions.cs`, add `using System.Text.RegularExpressions;` at the top, and inside the class after `CaptureQueryString` (line 22):

```csharp
    /// <summary>
    /// Capture request context (headers, cookies, query, form, server variables) into the stored
    /// <see cref="ExceptionEntry.RequestContext"/>. Off by default — it persists more request data, so
    /// it is opt-in (and runs through <see cref="Redactor"/>).
    /// </summary>
    public bool CaptureRequestContext { get; set; }

    /// <summary>
    /// Per key/value redaction applied to every captured request-context entry: returns the value to
    /// store, a masked value, or <see langword="null"/> to drop the entry. Defaults to
    /// <see cref="DefaultRedactor"/> (masks only categorical secrets). Set to <see langword="null"/> to
    /// capture everything verbatim (the host then owns that data-protection choice).
    /// </summary>
    public Func<string, string, string?>? Redactor { get; set; } = DefaultRedactor;

    private static readonly Regex SecretKey = new(
        "authorization|^cookie$|^set-cookie$|password|secret|token|apikey|session",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Default redactor: masks values whose key names a categorical secret
    /// (Authorization/Cookie/Set-Cookie or contains password/secret/token/apikey/session) to
    /// <c>"***"</c>; returns all other values unchanged.</summary>
    public static string? DefaultRedactor(string key, string value)
        => SecretKey.IsMatch(key) ? "***" : value;
```

- [ ] **Step 4: Record the new public members**

In `PublicAPI.Unshipped.txt` add:

```
Themia.Exceptional.ExceptionalOptions.CaptureRequestContext.get -> bool
Themia.Exceptional.ExceptionalOptions.CaptureRequestContext.set -> void
Themia.Exceptional.ExceptionalOptions.Redactor.get -> System.Func<string!, string!, string?>?
Themia.Exceptional.ExceptionalOptions.Redactor.set -> void
static Themia.Exceptional.ExceptionalOptions.DefaultRedactor(string! key, string! value) -> string?
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter RedactorTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Exceptional/ExceptionalOptions.cs src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt tests/Themia.Exceptional.Tests/RedactorTests.cs
git commit -m "feat: add CaptureRequestContext option and configurable Redactor"
```

---

### Task 3: Enricher captures + redacts request context

**Files:**
- Modify: `src/neutral/Themia.Exceptional/Serilog/HttpContextEnricher.cs`
- Test: `tests/Themia.Exceptional.Tests/HttpContextEnricherTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.Tests/HttpContextEnricherTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class HttpContextEnricherTests
{
    private sealed class FixedAccessor(HttpContext ctx) : IHttpContextAccessor
    { public HttpContext? HttpContext { get => ctx; set { } } }

    private static LogEvent NewEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Error, new Exception(),
        new MessageTemplate(Array.Empty<MessageTemplateToken>()),
        Array.Empty<LogEventProperty>());

    private static string? Prop(LogEvent e, string name)
        => e.Properties.TryGetValue(name, out var v) && v is ScalarValue s ? s.Value?.ToString() : null;

    [Fact]
    public void Captures_Headers_Redacted_WhenEnabled()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["User-Agent"] = "Edge";
        ctx.Request.Headers["Authorization"] = "Bearer secret";
        var options = new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = true };
        var sut = new HttpContextEnricher(new FixedAccessor(ctx), options);
        var e = NewEvent();

        sut.Enrich(e, new TestPropertyFactory());

        var json = Prop(e, "RequestContext");
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var headers = doc.RootElement.GetProperty("headers");
        Assert.Equal("Edge", headers.GetProperty("User-Agent").GetString());
        Assert.Equal("***", headers.GetProperty("Authorization").GetString());
    }

    [Fact]
    public void NoRequestContext_WhenDisabled()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["User-Agent"] = "Edge";
        var sut = new HttpContextEnricher(new FixedAccessor(ctx),
            new ExceptionalOptions { ApplicationName = "t", CaptureRequestContext = false });
        var e = NewEvent();

        sut.Enrich(e, new TestPropertyFactory());

        Assert.Null(Prop(e, "RequestContext"));
    }

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter HttpContextEnricherTests`
Expected: FAIL — no `RequestContext` property is produced.

- [ ] **Step 3: Implement capture in the enricher**

In `src/neutral/Themia.Exceptional/Serilog/HttpContextEnricher.cs`, add `using System.Text.Json;` and `using Microsoft.Extensions.Primitives;` at the top. After the `RequestBody` capture line (line 49) add:

```csharp
        if (options.CaptureRequestContext)
            Add(logEvent, propertyFactory, "RequestContext", BuildRequestContext(http, options.Redactor));
```

Then add these members to the class:

```csharp
    private static readonly JsonSerializerOptions ContextJson = new() { WriteIndented = false };

    private static string BuildRequestContext(HttpContext http, Func<string, string, string?>? redactor)
    {
        var request = http.Request;
        var ctx = new Dictionary<string, Dictionary<string, string?>>
        {
            ["headers"] = Collect(request.Headers, redactor),
            ["cookies"] = Collect(request.Cookies.Select(c => new KeyValuePair<string, StringValues>(c.Key, c.Value)), redactor),
            ["queryString"] = Collect(request.Query, redactor),
            ["form"] = TryForm(request, redactor),
            ["serverVariables"] = ServerVariables(http, redactor),
        };
        return JsonSerializer.Serialize(ctx, ContextJson);
    }

    private static Dictionary<string, string?> Collect(
        IEnumerable<KeyValuePair<string, StringValues>> pairs, Func<string, string, string?>? redactor)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, values) in pairs)
        {
            var raw = values.ToString();
            var stored = redactor is null ? raw : redactor(key, raw);
            if (stored is not null)
                map[key] = stored;
        }
        return map;
    }

    private static Dictionary<string, string?> TryForm(HttpRequest request, Func<string, string, string?>? redactor)
    {
        // Only read an already-buffered form; never force-read/rewind the body from the logging path.
        if (!request.HasFormContentType)
            return new Dictionary<string, string?>();
        try { return Collect(request.Form, redactor); }
        catch { return new Dictionary<string, string?>(); }
    }

    private static Dictionary<string, string?> ServerVariables(HttpContext http, Func<string, string, string?>? redactor)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REMOTE_ADDR"] = http.Connection.RemoteIpAddress?.ToString() ?? "",
            ["SERVER_NAME"] = http.Request.Host.Host,
            ["SERVER_PORT"] = http.Request.Host.Port?.ToString() ?? "",
            ["REQUEST_METHOD"] = http.Request.Method,
            ["SERVER_PROTOCOL"] = http.Request.Protocol,
        };
        return Collect(raw.Select(kv => new KeyValuePair<string, StringValues>(kv.Key, kv.Value)), redactor);
    }
```

(Note: the `BuildRequestContext` result is a non-empty JSON object even when groups are empty; `Add` only skips `null`/`""`, and this is neither, so the property is always written when capture is on — matching the test.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter HttpContextEnricherTests`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Exceptional/Serilog/HttpContextEnricher.cs tests/Themia.Exceptional.Tests/HttpContextEnricherTests.cs
git commit -m "feat: capture redacted request context in HttpContextEnricher"
```

---

### Task 4: Sink maps the `RequestContext` property onto the entry

**Files:**
- Modify: `src/neutral/Themia.Exceptional/Serilog/ExceptionalSerilogSink.cs:57`
- Test: `tests/Themia.Exceptional.Tests/ExceptionalSerilogSinkTests.cs` (add a case if the file exists; else create)

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.Tests/ExceptionalSerilogSinkContextTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Themia.Exceptional;
using Themia.Exceptional.Serilog;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class ExceptionalSerilogSinkContextTests
{
    private sealed class CapturingStore : IExceptionStore
    {
        public ExceptionEntry? Last;
        public Task LogAsync(ExceptionEntry e, CancellationToken ct = default) { Last = e; return Task.CompletedTask; }
        public Task<ExceptionEntry?> GetAsync(Guid g, CancellationToken ct = default) => Task.FromResult<ExceptionEntry?>(null);
        public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(new PagedResult<ExceptionEntry>([], 0));
        public Task<int> CountAsync(ExceptionFilter f, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> ProtectAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HardDeleteAsync(Guid g, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> PurgeAsync(DateTime o, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public void Emit_MapsRequestContextProperty()
    {
        var store = new CapturingStore();
        var options = new ExceptionalOptions { ApplicationName = "t" };
        var logger = new LoggerConfiguration()
            .Enrich.WithProperty("RequestContext", "{\"headers\":{\"A\":\"1\"}}")
            .WriteTo.Sink(new ExceptionalSerilogSink(store, options))
            .CreateLogger();

        logger.Error(new InvalidOperationException("boom"), "err");

        Assert.Equal("{\"headers\":{\"A\":\"1\"}}", store.Last!.RequestContext);
    }
}
```

(Verify `PagedResult<T>` has a `(items, total)` constructor; if its shape differs, adjust the fake's `ListAsync` to match the real type — check `src/neutral/Themia.Exceptional/PagedResult.cs`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter Emit_MapsRequestContextProperty`
Expected: FAIL — `RequestContext` stays null.

- [ ] **Step 3: Map the property**

In `src/neutral/Themia.Exceptional/Serilog/ExceptionalSerilogSink.cs`, in `ApplyContext` after the `RequestBody` line (line 57):

```csharp
        entry.RequestContext = Read(logEvent, "RequestContext");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.Tests --filter Emit_MapsRequestContextProperty`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Exceptional/Serilog/ExceptionalSerilogSink.cs tests/Themia.Exceptional.Tests/ExceptionalSerilogSinkContextTests.cs
git commit -m "feat: persist captured RequestContext from the Serilog sink"
```

---

### Task 5: Forward-only migration adds the `RequestContext` column

**Files:**
- Create: `src/neutral/Themia.Exceptional/Migrations/AddRequestContextColumn.cs`

The deployed `ExceptionLogMigration` is never edited (forward-only). This adds one nullable large-text column across the three supported engines using the same lockstep guard.

- [ ] **Step 1: Create the migration**

Create `src/neutral/Themia.Exceptional/Migrations/AddRequestContextColumn.cs`:

```csharp
using FluentMigrator;

namespace Themia.Exceptional.Migrations;

/// <summary>Adds the nullable <c>RequestContext</c> column (JSON request context) to <c>Exceptions</c>.
/// Additive + nullable, so it is safe on existing rows and existing engines.</summary>
[Migration(202606220001, "Themia.Exceptional: add RequestContext column")]
public sealed class AddRequestContextColumn : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP with ExceptionLogMigration's provider whitelist (PostgreSQL/MySQL/SqlServer).
        IfDatabase("postgresql", "mysql", "sqlserver")
            .Alter.Table("Exceptions")
            .AddColumn("RequestContext").AsString(int.MaxValue).Nullable();

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Exceptional supports only PostgreSQL, MySQL/MariaDB, and SQL Server."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Column("RequestContext").FromTable("Exceptions");
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/neutral/Themia.Exceptional/Themia.Exceptional.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/neutral/Themia.Exceptional/Migrations/AddRequestContextColumn.cs
git commit -m "feat: migration adding RequestContext column (3 engines)"
```

---

### Task 6: Integration test — migrate + round-trip across all engines

**Files:**
- Test: `tests/Themia.Exceptional.IntegrationTests/RequestContextRoundTripTests.cs`

Mirror the existing integration-test fixtures in `tests/Themia.Exceptional.IntegrationTests` (find how they spin a Testcontainers container per engine, run `ThemiaMigrations.Run`, and resolve `IExceptionStore`). Reuse those fixtures; do not invent a new container harness.

- [ ] **Step 1: Write the test**

Create `tests/Themia.Exceptional.IntegrationTests/RequestContextRoundTripTests.cs` (adapt the fixture/base type names to the ones already in this project):

```csharp
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.IntegrationTests;

[Trait("Category", "Integration")]
public abstract class RequestContextRoundTripTestsBase
{
    protected abstract IExceptionStore Store { get; }

    [Fact]
    public async Task Insert_And_Get_RoundTripsRequestContext()
    {
        var entry = ExceptionEntryFactory.FromException(new InvalidOperationException("ctx"), "IntegrationApp");
        entry.RequestContext = "{\"headers\":{\"User-Agent\":\"Edge\"},\"cookies\":{}}";

        await Store.LogAsync(entry);
        var fetched = await Store.GetAsync(entry.Guid);

        Assert.NotNull(fetched);
        Assert.Equal(entry.RequestContext, fetched!.RequestContext);
    }
}
```

Then add one concrete subclass per engine, wired to the existing per-engine fixture (e.g. `PostgresRequestContextRoundTripTests : RequestContextRoundTripTestsBase, IClassFixture<PostgresExceptionStoreFixture>` — use whatever fixture the existing tests use, and have the fixture run BOTH `ExceptionLogMigration` and `AddRequestContextColumn` via `ThemiaMigrations.Run(engine, conn, typeof(ExceptionLogMigration).Assembly)`, which discovers both). Provide SqlServer and MySql subclasses likewise.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Themia.Exceptional.IntegrationTests --filter RequestContext`
Expected: PASS on all three engines (first run pulls the DB images; allow time).

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Exceptional.IntegrationTests/RequestContextRoundTripTests.cs
git commit -m "test: RequestContext round-trips across all 3 engines"
```

---

### Task 7: Embedded CSS for the SE look

**Files:**
- Create: `src/neutral/Themia.Exceptional.AspNetCore/DashboardCss.cs`
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs:15-23` (link CSS instead of inline `<style>`)
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardEndpoints.cs` (serve CSS route)
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/CssEndpointTests.cs`

Keep CSP-friendliness: serve the CSS from a route and link it (`<link rel="stylesheet">`), no inline `<style>`/`<script>`. A single C# string constant avoids EmbeddedResource wiring while staying self-contained.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/CssEndpointTests.cs` (follow the existing dashboard test setup in this project — `WebApplicationFactory` + a fake `IExceptionStore` + `MapThemiaExceptional` with `Authorize` returning true):

```csharp
using System.Net;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class CssEndpointTests
{
    [Fact]
    public async Task Css_IsServed_WithCssContentType()
    {
        using var app = DashboardTestHost.Create(authorize: _ => Task.FromResult(true)); // reuse existing helper
        var client = app.GetTestClient();

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/css", res.Content.Headers.ContentType!.ToString());
        Assert.Contains("table", await res.Content.ReadAsStringAsync());
    }
}
```

(If no `DashboardTestHost` helper exists, build the host inline as the other tests in `Themia.Exceptional.AspNetCore.Tests` do.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter Css_IsServed`
Expected: FAIL — 404, no CSS route.

- [ ] **Step 3: Create the CSS**

Create `src/neutral/Themia.Exceptional.AspNetCore/DashboardCss.cs`:

```csharp
namespace Themia.Exceptional.AspNetCore;

/// <summary>The dashboard stylesheet, served from a route and linked (CSP-friendly: no inline style).</summary>
internal static class DashboardCss
{
    internal const string Content = """
        :root{--fg:#24292f;--muted:#57606a;--line:#d0d7de;--bg:#fff;--accent:#0969da;--err:#cf222e}
        body{font:14px -apple-system,system-ui,sans-serif;color:var(--fg);margin:1.5rem;background:var(--bg)}
        h1{font-size:1.4rem;margin:0 0 .25rem}
        .summary{color:var(--muted);margin:0 0 1rem}
        table{border-collapse:collapse;width:100%}
        th,td{border-bottom:1px solid var(--line);padding:6px 10px;text-align:left;vertical-align:top}
        th{background:#f6f8fa;font-weight:600}
        tr:hover td{background:#f6f8fa}
        a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
        pre{background:#f6f8fa;border:1px solid var(--line);border-radius:6px;padding:10px;overflow:auto;white-space:pre-wrap;font:12px ui-monospace,Menlo,Consolas,monospace}
        .type{font-weight:600}.type-err{color:var(--err)}
        .meta th{width:160px;white-space:nowrap}
        form.filter{margin:0 0 1rem;display:flex;gap:.5rem;flex-wrap:wrap}
        input,button{font:14px inherit;padding:4px 8px;border:1px solid var(--line);border-radius:6px}
        button{background:#f6f8fa;cursor:pointer}
        .actions{display:inline}.actions button{color:var(--err)}
        time{cursor:help}
        h2{font-size:1.05rem;margin:1.25rem 0 .25rem;border-bottom:1px solid var(--line);padding-bottom:.2rem}
        """;
}
```

- [ ] **Step 4: Serve the CSS route**

In `ExceptionalDashboardEndpoints.cs`, in `MapThemiaExceptional` after the detail `MapGet` (line 46), add:

```csharp
        group.MapGet("dashboard.css", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/css; charset=utf-8";
            return ctx.Response.WriteAsync(DashboardCss.Content);
        });
```

(The CSS is non-sensitive static text; it is intentionally readable without the `Authorize` gate so the linked stylesheet loads. No exception data is exposed.)

- [ ] **Step 5: Link the CSS in `Page`**

In `DashboardHtml.cs`, replace the `Style` constant + its use in `Page` (lines 15-23). `Page` needs the mount path to build the link, so change its signature and callers:

```csharp
    internal static string Page(string title, string path, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Enc(title) +
        "</title><link rel=\"stylesheet\" href=\"" + Enc(path) + "/dashboard.css\"></head><body>" + body + "</body></html>";
```

Delete the `Style` constant. Update the two `Page(title, ...)` call sites in `List` and `Detail` to `Page(title, path, sb.ToString())` (both methods already receive `path`).

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter Css_IsServed`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore tests/Themia.Exceptional.AspNetCore.Tests/CssEndpointTests.cs
git commit -m "feat: serve dashboard CSS from a route and link it (CSP-friendly)"
```

---

### Task 8: Dashboard options — `EnableActions`, `ShowRequestContext`

**Files:**
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardOptions.cs`
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/DashboardOptionsDefaultsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/DashboardOptionsDefaultsTests.cs`:

```csharp
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DashboardOptionsDefaultsTests
{
    [Fact]
    public void Defaults_EnableActionsAndShowRequestContext_True()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.True(o.EnableActions);
        Assert.True(o.ShowRequestContext);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter DashboardOptionsDefaults`
Expected: FAIL — members don't exist.

- [ ] **Step 3: Add the options**

In `ExceptionalDashboardOptions.cs`, after `ShowRequestBody` (line 24):

```csharp
    /// <summary>Whether protect/delete actions (POST) are exposed in the UI and accepted. Default <c>true</c>.
    /// Still gated by <see cref="Authorize"/> and a same-origin double-submit token.</summary>
    public bool EnableActions { get; set; } = true;

    /// <summary>Whether the detail view renders the captured request-context sections (headers/cookies/
    /// query/form/server variables) when present. Default <c>true</c>.</summary>
    public bool ShowRequestContext { get; set; } = true;
```

- [ ] **Step 4: Record the new public members**

In `src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt` add:

```
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.EnableActions.get -> bool
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.EnableActions.set -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.ShowRequestContext.get -> bool
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.ShowRequestContext.set -> void
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter DashboardOptionsDefaults`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardOptions.cs src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt tests/Themia.Exceptional.AspNetCore.Tests/DashboardOptionsDefaultsTests.cs
git commit -m "feat: add EnableActions and ShowRequestContext dashboard options"
```

---

### Task 9: Detail view — formatted stack trace + request-context sections

**Files:**
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs` (`Detail`)
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/DetailRenderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/DetailRenderTests.cs`:

```csharp
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DetailRenderTests
{
    private static ExceptionEntry Entry() => new()
    {
        Type = "System.InvalidOperationException",
        Message = "boom",
        Detail = "{\"Message\":\"boom\",\"Type\":\"System.InvalidOperationException\",\"StackTrace\":\"at A()\\n at B()\",\"Inner\":null,\"Data\":null}",
        RequestContext = "{\"headers\":{\"User-Agent\":\"<b>Edge</b>\"},\"cookies\":{},\"queryString\":{},\"form\":{},\"serverVariables\":{\"REMOTE_ADDR\":\"::1\"}}",
    };

    [Fact]
    public void Detail_RendersStackTraceWithLineBreaks_NotRawJson()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: true);

        Assert.Contains("at A()\n at B()", html);          // real newline from the parsed StackTrace
        Assert.DoesNotContain("\\\"StackTrace\\\"", html);  // not the raw escaped-JSON blob
    }

    [Fact]
    public void Detail_RendersRequestContextSections_Encoded()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: true);

        Assert.Contains("Request Headers", html);
        Assert.Contains("Server Variables", html);
        Assert.Contains("&lt;b&gt;Edge&lt;/b&gt;", html);   // header value HTML-encoded
        Assert.DoesNotContain("<b>Edge</b>", html);
    }

    [Fact]
    public void Detail_OmitsRequestContext_WhenDisabled()
    {
        var html = DashboardHtml.Detail("Exceptions", "/exceptions", Entry(), showRequestBody: true, showRequestContext: false);
        Assert.DoesNotContain("Request Headers", html);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter DetailRenderTests`
Expected: FAIL — `Detail` has the old signature and dumps raw JSON.

- [ ] **Step 3: Rewrite `Detail`**

In `DashboardHtml.cs`, add `using System.Text.Json;` and replace the `Detail` method (lines 70-101) with:

```csharp
    internal static string Detail(string title, string path, ExceptionEntry e, bool showRequestBody, bool showRequestContext)
    {
        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Enc(path)).Append("\">&larr; back</a></p>");
        sb.Append("<h1 class=\"type type-err\">").Append(Enc(e.Type)).Append("</h1>");
        sb.Append("<p>").Append(Enc(e.Message)).Append("</p>");

        sb.Append("<table class=\"meta\">");
        Row(sb, "Guid", e.Guid.ToString());
        Row(sb, "Application", e.ApplicationName);
        Row(sb, "Machine", e.MachineName);
        Row(sb, "Tenant", e.TenantId);
        Row(sb, "Status", e.StatusCode?.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Method", e.HttpMethod);
        Row(sb, "Url", e.Url);
        Row(sb, "Host", e.Host);
        Row(sb, "IP", e.IpAddress);
        Row(sb, "Source", e.Source);
        Row(sb, "Count", e.DuplicateCount.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Created", e.CreationDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Last log", e.LastLogDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Protected", e.IsProtected.ToString());
        sb.Append("</table>");

        // Parse the stored Detail JSON and render the stack trace with real line breaks (the old UI
        // dumped the whole escaped-JSON blob). Fall back to the raw text if it is not valid JSON.
        var (stackTrace, inner, data) = ParseDetail(e.Detail);
        if (stackTrace is not null)
        {
            sb.Append("<h2>Stack Trace</h2><pre>").Append(Enc(stackTrace)).Append("</pre>");
            if (!string.IsNullOrEmpty(inner))
                sb.Append("<h2>Inner Exception</h2><pre>").Append(Enc(inner)).Append("</pre>");
            if (!string.IsNullOrEmpty(data))
                sb.Append("<h2>Data</h2><pre>").Append(Enc(data)).Append("</pre>");
        }
        else
        {
            sb.Append("<h2>Detail</h2><pre>").Append(Enc(e.Detail)).Append("</pre>");
        }

        if (showRequestBody && e.RequestBody is not null)
            sb.Append("<h2>Request Body</h2><pre>").Append(Enc(e.RequestBody)).Append("</pre>");

        if (showRequestContext && e.RequestContext is not null)
            AppendRequestContext(sb, e.RequestContext);

        return Page(title, path, sb.ToString());
    }

    private static (string? StackTrace, string? Inner, string? Data) ParseDetail(string detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            var root = doc.RootElement;
            string? Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? data = root.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.Object ? d.GetRawText() : null;
            return (Get("StackTrace"), Get("Inner"), data);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static readonly (string Key, string Heading)[] ContextGroups =
    {
        ("serverVariables", "Server Variables"),
        ("requestHeaders", "Request Headers"), // tolerate either key spelling
        ("headers", "Request Headers"),
        ("cookies", "Cookies"),
        ("queryString", "QueryString"),
        ("form", "Form"),
    };

    private static void AppendRequestContext(StringBuilder sb, string requestContext)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(requestContext); }
        catch (JsonException) { return; }
        using (doc)
        {
            foreach (var (key, heading) in ContextGroups)
            {
                if (!doc.RootElement.TryGetProperty(key, out var group) || group.ValueKind != JsonValueKind.Object)
                    continue;
                var rows = new StringBuilder();
                foreach (var prop in group.EnumerateObject())
                    rows.Append("<tr><th>").Append(Enc(prop.Name)).Append("</th><td>")
                        .Append(Enc(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText()))
                        .Append("</td></tr>");
                if (rows.Length == 0)
                    continue;
                sb.Append("<h2>").Append(Enc(heading)).Append("</h2><table>").Append(rows).Append("</table>");
            }
        }
    }
```

(`Row` and `Enc` already exist. The `headers`/`requestHeaders` duplicate in `ContextGroups` both map to "Request Headers" so whichever key the enricher emitted renders once — the enricher emits `headers`.)

- [ ] **Step 4: Update the caller**

In `ExceptionalDashboardEndpoints.cs` `HandleDetailAsync` (line 66), pass the new arg:

```csharp
        await WriteHtmlAsync(ctx, DashboardHtml.Detail(options.Title, path, entry, options.ShowRequestBody, options.ShowRequestContext), ct).ConfigureAwait(false);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter DetailRenderTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore tests/Themia.Exceptional.AspNetCore.Tests/DetailRenderTests.cs
git commit -m "feat: detail view renders formatted stack trace + request-context sections"
```

---

### Task 10: List view — relative time, type accent, summary header

**Files:**
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs` (`List`)
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/ListRenderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/ListRenderTests.cs`:

```csharp
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class ListRenderTests
{
    [Fact]
    public void List_RendersSummaryAndRelativeTime()
    {
        var now = DateTime.UtcNow;
        var items = new List<ExceptionEntry>
        {
            new() { Guid = Guid.NewGuid(), Type = "System.Exception", Message = "m", ApplicationName = "App",
                    LastLogDate = now.AddSeconds(-14), DuplicateCount = 2 },
        };
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };

        var html = DashboardHtml.List("Exceptions", "/exceptions", items, total: 200, filter, now);

        Assert.Contains("200 errors", html);                       // summary header
        Assert.Contains("secs ago", html);                          // relative time
        Assert.Contains("<time", html);                             // absolute on hover via <time title=…>
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter ListRenderTests`
Expected: FAIL — `List` has no `now` param / no summary / no relative time.

- [ ] **Step 3: Update `List`**

In `DashboardHtml.cs`, change `List` to accept `DateTime utcNow` and render the summary + relative time. Replace the signature and the header/row-time bits:

```csharp
    internal static string List(string title, string path, IReadOnlyList<ExceptionEntry> items, int total, ExceptionFilter filter, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(Enc(title)).Append("</h1>");

        var last = items.Count > 0 ? Relative(items[0].LastLogDate, utcNow) : "—";
        sb.Append("<p class=\"summary\"><strong>").Append(total).Append(" errors</strong> (last: ").Append(Enc(last)).Append(")</p>");

        sb.Append("<form class=\"filter\" method=\"get\" action=\"").Append(Enc(path)).Append("\">")
          .Append("<input name=\"q\" value=\"").Append(Enc(filter.Search)).Append("\" placeholder=\"search\"> ")
          .Append("<input name=\"app\" value=\"").Append(Enc(filter.ApplicationName)).Append("\" placeholder=\"app\"> ")
          .Append("<input name=\"tenant\" value=\"").Append(Enc(filter.TenantId)).Append("\" placeholder=\"tenant\"> ")
          .Append("<button type=\"submit\">Filter</button></form>");

        sb.Append("<table><tr><th>Last log</th><th>App</th><th>Type</th><th>Message</th><th>Status</th><th>Count</th><th>Tenant</th></tr>");
        foreach (var e in items)
        {
            sb.Append("<tr>")
              .Append("<td><time title=\"").Append(Enc(e.LastLogDate.ToString("u", CultureInfo.InvariantCulture))).Append("\">")
              .Append(Enc(Relative(e.LastLogDate, utcNow))).Append("</time></td>")
              .Append("<td>").Append(Enc(e.ApplicationName)).Append("</td>")
              .Append("<td class=\"type type-err\"><a href=\"").Append(Enc(path)).Append('/').Append(e.Guid).Append("\">").Append(Enc(e.Type)).Append("</a></td>")
              .Append("<td>").Append(Enc(e.Message)).Append("</td>")
              .Append("<td>").Append(Enc(e.StatusCode?.ToString(CultureInfo.InvariantCulture))).Append("</td>")
              .Append("<td>").Append(e.DuplicateCount).Append("</td>")
              .Append("<td>").Append(Enc(e.TenantId)).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</table>");

        var hasPrev = filter.Page > 1;
        var hasNext = (long)filter.Page * filter.PageSize < total;
        sb.Append("<p>");
        if (hasPrev)
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page - 1)
              .Append("&amp;pageSize=").Append(filter.PageSize).Append("\">Prev</a> ");
        sb.Append("Page ").Append(filter.Page).Append(" (").Append(total).Append(" total) ");
        if (hasNext)
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page + 1)
              .Append("&amp;pageSize=").Append(filter.PageSize).Append("\">Next</a>");
        sb.Append("</p>");

        return Page(title, path, sb.ToString());
    }

    private static string Relative(DateTime utc, DateTime now)
    {
        var span = now - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds} secs ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} mins ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hours ago";
        return $"{(int)span.TotalDays} days ago";
    }
```

- [ ] **Step 4: Update the caller**

In `ExceptionalDashboardEndpoints.cs` `HandleListAsync` (line 56):

```csharp
        await WriteHtmlAsync(ctx, DashboardHtml.List(options.Title, path, result.Items, result.Total, filter, DateTime.UtcNow), ct).ConfigureAwait(false);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter ListRenderTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardEndpoints.cs tests/Themia.Exceptional.AspNetCore.Tests/ListRenderTests.cs
git commit -m "feat: list view with summary header, relative time, type accent"
```

---

### Task 11: Protect / delete actions with double-submit CSRF

**Files:**
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardEndpoints.cs`
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs` (render action buttons + hidden token; `Detail` gains a token param)
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/ActionCsrfTests.cs`

Self-contained CSRF (no host antiforgery dependency): each rendered page issues a random token, set as a `SameSite=Strict` cookie **and** an `<input type="hidden" name="__token">`; POST handlers require the two to match and the `Origin`/`Referer` host to match the request host.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/ActionCsrfTests.cs` (use the existing dashboard test host helper + fake store that records calls):

```csharp
using System.Net;
using System.Net.Http;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class ActionCsrfTests
{
    [Fact]
    public async Task Post_WithoutToken_IsRejected()
    {
        using var app = DashboardTestHost.Create(authorize: _ => Task.FromResult(true));
        var client = app.GetTestClient();

        var res = await client.PostAsync($"/exceptions/{Guid.NewGuid()}/protect", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_WhenAuthorizeDenies_Returns404_EvenWithToken()
    {
        using var app = DashboardTestHost.Create(authorize: _ => Task.FromResult(false));
        var client = app.GetTestClient();

        // even a well-formed token can't bypass the auth gate
        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/protect")
        { Content = new FormUrlEncodedContent([new("__token", "x")]) };
        req.Headers.Add("Cookie", "__themia_csrf=x");
        req.Headers.Add("Origin", "http://localhost");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Post_WithMatchingTokenAndOrigin_InvokesStoreAndRedirects()
    {
        var store = new RecordingStore(); // fake recording ProtectAsync(guid)
        using var app = DashboardTestHost.Create(authorize: _ => Task.FromResult(true), store: store);
        var client = app.GetTestClient();
        var guid = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{guid}/protect")
        { Content = new FormUrlEncodedContent([new("__token", "tok")]) };
        req.Headers.Add("Cookie", "__themia_csrf=tok");
        req.Headers.Add("Origin", "http://localhost");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode); // 303 back to list
        Assert.Contains(guid, store.Protected);
    }
}
```

(Extend the test project's existing fake `IExceptionStore` to record `ProtectAsync`/`DeleteAsync`/`HardDeleteAsync` guids; the `RecordingStore`/`DashboardTestHost` names should match whatever the project already uses.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter ActionCsrfTests`
Expected: FAIL — no POST routes.

- [ ] **Step 3: Add the CSRF helper + POST routes**

In `ExceptionalDashboardEndpoints.cs` add `using System.Security.Cryptography;` and, inside `MapThemiaExceptional` after the css route, register the actions when enabled:

```csharp
        if (options.EnableActions)
        {
            group.MapPost("{guid:guid}/protect", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.ProtectAsync, ct));
            group.MapPost("{guid:guid}/delete", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.DeleteAsync, ct));
            group.MapPost("{guid:guid}/hard-delete", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) =>
                HandleActionAsync(ctx, options, path, guid, store.HardDeleteAsync, ct));
        }
```

Add the handler + helpers:

```csharp
    private const string CsrfCookie = "__themia_csrf";

    private static async Task HandleActionAsync(
        HttpContext ctx, ExceptionalDashboardOptions options, string path, Guid guid,
        Func<Guid, CancellationToken, Task<bool>> action, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!ValidCsrf(ctx)) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

        await action(guid, ct).ConfigureAwait(false);
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = path; // back to the list
    }

    private static bool ValidCsrf(HttpContext ctx)
    {
        var cookie = ctx.Request.Cookies[CsrfCookie];
        var form = ctx.Request.HasFormContentType ? ctx.Request.Form["__token"].ToString() : null;
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(form) ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(cookie), System.Text.Encoding.UTF8.GetBytes(form)))
            return false;
        // Same-origin: Origin (preferred) or Referer host must equal the request host.
        var host = ctx.Request.Host.Value;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
            return Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase);
        var referer = ctx.Request.Headers.Referer.ToString();
        return !string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r) && string.Equals(r.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    // Issues (and persists) the per-session CSRF token, returning it for embedding in rendered forms.
    private static string IssueCsrf(HttpContext ctx)
    {
        var existing = ctx.Request.Cookies[CsrfCookie];
        if (!string.IsNullOrEmpty(existing)) return existing;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ctx.Response.Cookies.Append(CsrfCookie, token, new CookieOptions
        { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = "/" });
        return token;
    }
```

Update `HandleListAsync` and `HandleDetailAsync` to issue the token and pass it to the renderer:

```csharp
        var token = options.EnableActions ? IssueCsrf(ctx) : null;
        // list:
        await WriteHtmlAsync(ctx, DashboardHtml.List(options.Title, path, result.Items, result.Total, filter, DateTime.UtcNow, token), ct).ConfigureAwait(false);
        // detail:
        await WriteHtmlAsync(ctx, DashboardHtml.Detail(options.Title, path, entry, options.ShowRequestBody, options.ShowRequestContext, token), ct).ConfigureAwait(false);
```

- [ ] **Step 4: Render action buttons (DashboardHtml)**

Add `string? csrfToken = null` as the last, **optional** parameter of both `List` and `Detail` (optional so the Task 9/10 tests that call them without a token still compile). In `Detail`, after the meta table, when `csrfToken is not null` render a small action form:

```csharp
        if (csrfToken is not null)
        {
            sb.Append("<form class=\"actions\" method=\"post\" action=\"").Append(Enc(path)).Append('/').Append(e.Guid).Append("/protect\">")
              .Append("<input type=\"hidden\" name=\"__token\" value=\"").Append(Enc(csrfToken)).Append("\">")
              .Append("<button type=\"submit\">").Append(e.IsProtected ? "Protected" : "Protect").Append("</button></form> ");
            sb.Append("<form class=\"actions\" method=\"post\" action=\"").Append(Enc(path)).Append('/').Append(e.Guid).Append("/delete\" onsubmit=\"return confirm('Delete?')\">")
              .Append("<input type=\"hidden\" name=\"__token\" value=\"").Append(Enc(csrfToken)).Append("\">")
              .Append("<button type=\"submit\">Delete</button></form>");
        }
```

(`onsubmit="return confirm(...)"` is an inline handler attribute, not an inline `<script>` block — it's allowed under a script-src CSP without `unsafe-inline` only if the consumer permits inline event handlers; if strict CSP is required, the consumer can drop it. Keep it minimal; the action still works without JS.) In `List`, the `csrfToken` is currently unused for per-row buttons in v1 (row actions can be added later); accept the parameter to keep the signatures aligned and avoid a second render path.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests --filter ActionCsrfTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore tests/Themia.Exceptional.AspNetCore.Tests/ActionCsrfTests.cs
git commit -m "feat: protect/delete actions with self-contained double-submit CSRF"
```

---

### Task 12: Version bump, changelog, full build + test

**Files:**
- Modify: `Directory.Build.props` (line 26)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.6.0</Version>` to `<Version>0.6.1</Version>`.

- [ ] **Step 2: Add the changelog entry**

In `CHANGELOG.md`, add a new section directly under `## [Unreleased]`:

```markdown
## [0.6.1] - 2026-06-22

### Added
- `Themia.Exceptional` — opt-in request-context capture (`ExceptionalOptions.CaptureRequestContext`)
  recording request headers, cookies, query, form, and server variables into a new nullable
  `RequestContext` column, with a configurable `Redactor` (default masks Authorization/Cookie/secret-named
  values; set to `null` to capture raw). New forward-only migration adds the column across SQL Server /
  MySQL / PostgreSQL.
- `Themia.Exceptional.AspNetCore` — StackExchange.Exceptional-style dashboard: formatted stack trace,
  request-context sections (Server Variables / Headers / Cookies / Query / Form), relative time + summary
  header in the list, and protect/delete actions (POST behind a self-contained double-submit CSRF token).
  New options `EnableActions` and `ShowRequestContext`.

### Security
- Request-context capture is **off by default**; the default `Redactor` keeps Authorization tokens and
  session cookies out of the store. Consumers opting in own the data-protection trade-off.
```

- [ ] **Step 3: Full clean build + test**

Run:
```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet build Themia.sln --no-incremental
dotnet test tests/Themia.Exceptional.Tests tests/Themia.Exceptional.AspNetCore.Tests
```
Expected: solution builds with 0 warnings (TreatWarningsAsErrors); all unit tests pass. (Integration tests run separately per Task 6.)

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props CHANGELOG.md
git commit -m "chore: release 0.6.1 — Exceptional dashboard SE-parity + request-context capture"
```

---

## Post-implementation (controller, not a task step)

- Open the PR for `feature/themia-exceptional-dashboard-parity`; run the standard review passes
  (`/code-review`, `/pr-review-toolkit:review-pr`, `/agy-review`) — pay special attention to the CSRF
  handler, the enricher's defensive form access, and XSS encoding of the new sections.
- Advance the coord thread (ezy → enable `CaptureRequestContext`, adopt the upgraded dashboard).
