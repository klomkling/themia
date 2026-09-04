# Themia Sequences Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Framework.Data.Sequences` — an atomic, tenant-scoped document-number allocator whose value survives the caller's rollback, so two concurrent callers can never receive the same invoice number.

**Architecture:** One ORM-agnostic package. The provider opens its **own** `DbConnection` and transaction (that is the whole semantic — the allocated number must survive the outer caller's rollback), runs per-engine locking SQL through an `ISequenceDialect`, and stores counters in one table keyed `(tenant_id, sequence_key)`. Engine is chosen at runtime by an enum, following `Themia.Data.Migrations`, not by separate per-engine packages — the allocator binds to no ORM, so it needs no peer split.

**Tech Stack:** .NET 10, Dapper, FluentMigrator, Npgsql / MySqlConnector / Microsoft.Data.SqlClient, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-05-themia-sequences-design.md` — read it before starting. It records why three lines of §F in the architecture overview are stale, and why the ambient tenant must not silently fall back to the host counter.

## Global Constraints

- Target framework `net10.0`. Framework-layer packages are net10-only.
- `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` come from `Directory.Build.props`. **A warning fails the build.**
- Central package management: versions live in `Directory.Packages.props`. Never put a `Version=` on a `PackageReference`.
- `System.Text.Json` only. **Never** `Newtonsoft.Json`.
- `ILogger<T>` only. No `Console.WriteLine`.
- Every public member needs an XML doc comment, and every public API line must be added to `PublicAPI.Unshipped.txt` or the build fails `RS0016`.
- Schema/DDL is owned by **FluentMigrator**. No `dotnet ef migrations add`.
- Table name: **`themia_sequences`**, unqualified (default schema). This matches the other framework-level tables — `themia_version_<assembly>` and `data_protection_keys` — which deliberately avoid a schema so consumers with a non-default `search_path` are not split (coord #0088).
- Supported engines: PostgreSQL, MySQL 8.0.13+, SQL Server. **Not MariaDB.**
- Migrations are forward-only and must **adopt** an existing object rather than fail on it (coord #0078, #0085, #0096).
- Version bump at the end: `0.21.4` → `0.22.0` in `Directory.Build.props` (new package = MINOR).

---

### Task 1: Package scaffold, options, and the dialect contract

**Files:**
- Create: `src/framework/Themia.Framework.Data.Sequences/Themia.Framework.Data.Sequences.csproj`
- Create: `src/framework/Themia.Framework.Data.Sequences/SequenceEngine.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/SequenceOptions.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/ISequenceProvider.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/ISequenceDialect.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/PublicAPI.Shipped.txt` (empty)
- Create: `src/framework/Themia.Framework.Data.Sequences/PublicAPI.Unshipped.txt` (starts with `#nullable enable`)
- Create: `tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj`
- Create: `tests/Themia.Framework.Data.Sequences.Tests/SequenceOptionsTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `SequenceEngine` (enum: `Postgres`, `MySql`, `SqlServer`); `SequenceOptions` with `string ConnectionString { get; set; }`, `SequenceEngine Engine { get; set; }`, `ISequenceDialect? Dialect { get; set; }` and `void Validate()`; `ISequenceProvider` with the six methods below; `ISequenceDialect` (the per-engine seam — defined here rather than in Task 2 because `SequenceOptions.Dialect` refers to it).

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.Tests/SequenceOptionsTests.cs`:

```csharp
using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class SequenceOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankConnectionString(string connectionString)
    {
        var options = new SequenceOptions { ConnectionString = connectionString, Engine = SequenceEngine.Postgres };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsUndefinedEngine()
    {
        var options = new SequenceOptions { ConnectionString = "Host=x", Engine = (SequenceEngine)99 };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Engine", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsAConfiguredPair() =>
        new SequenceOptions { ConnectionString = "Host=x", Engine = SequenceEngine.Postgres }.Validate();

    [Fact]
    public void Validate_AcceptsACustomDialectWithNoKnownEngine()
    {
        // The reason ISequenceDialect is public: an adopter on an engine Themia does not ship supplies
        // one rather than forking. Without this the public interface has no way in and is decoration.
        var options = new SequenceOptions
        {
            ConnectionString = "whatever",
            Engine = (SequenceEngine)99,
            Dialect = new FakeDialect(),
        };

        options.Validate();
    }

    private sealed class FakeDialect : ISequenceDialect
    {
        public System.Data.Common.DbConnection CreateConnection(string connectionString) =>
            throw new NotSupportedException("not opened in this test");

        public string SelectForUpdateSql => "SELECT next_value ... @tenant ... @key";
        public string UpdateNextValueSql => "UPDATE ... @tenant ... @key ... @val";
        public string InsertIfMissingSql => "INSERT ... @tenant ... @key ... @val";
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj`
Expected: FAIL — the project does not exist yet.

- [ ] **Step 3: Create the project files**

`src/framework/Themia.Framework.Data.Sequences/Themia.Framework.Data.Sequences.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Sequences</PackageId>
    <Description>Atomic, tenant-scoped document-number allocation for Themia. Allocates in its own transaction so the number survives the caller's rollback: gaps are normal, duplicates are not. PostgreSQL, MySQL and SQL Server; works with either data peer.</Description>
    <PackageTags>themia;sequence;document-number;invoice;multi-tenancy</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Core/Themia.Framework.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="MySqlConnector" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
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
    <InternalsVisibleTo Include="Themia.Framework.Data.Sequences.Tests" />
    <InternalsVisibleTo Include="Themia.Framework.Data.Sequences.IntegrationTests" />
  </ItemGroup>
</Project>
```

`SequenceEngine.cs`:

```csharp
namespace Themia.Framework.Data.Sequences;

/// <summary>The database engine a <see cref="ISequenceProvider"/> allocates against.</summary>
/// <remarks>
/// An enum rather than a per-engine package: the allocator binds to no ORM, so there is nothing to split
/// along. This mirrors <c>Themia.Data.Migrations</c>' <c>MigrationEngine</c>, which every Themia app
/// already references.
/// </remarks>
public enum SequenceEngine
{
    /// <summary>PostgreSQL.</summary>
    Postgres = 0,

    /// <summary>MySQL 8.0.13 or later. MariaDB is not supported.</summary>
    MySql = 1,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer = 2,
}
```

`SequenceOptions.cs`:

```csharp
namespace Themia.Framework.Data.Sequences;

/// <summary>Configuration for <see cref="ISequenceProvider"/>.</summary>
public sealed class SequenceOptions
{
    /// <summary>
    /// Connection string the allocator opens its OWN connection with. Normally the same one the app gives
    /// the migration runner.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate setting rather than borrowing the data peer's connection: borrowing would
    /// put the allocation inside the caller's ambient transaction, and a rollback would then reissue the
    /// number to the next caller.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The database engine. No default is assumed — an unset value fails validation.</summary>
    /// <remarks>Ignored when <see cref="Dialect"/> is set.</remarks>
    public SequenceEngine Engine { get; set; }

    /// <summary>
    /// A custom dialect, for an engine Themia does not ship. When set, <see cref="Engine"/> is not used.
    /// </summary>
    /// <remarks>
    /// This is what makes <see cref="ISequenceDialect"/> worth being public: an adopter on an unsupported
    /// engine supplies one here instead of forking the package. Null for the three built-in engines.
    /// </remarks>
    public ISequenceDialect? Dialect { get; set; }

    /// <summary>Throws when the options cannot be used.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing or out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "SequenceOptions.ConnectionString is required. Set it to the database the "
                + "themia_sequences table lives in — normally the same connection string you pass to "
                + "ThemiaMigrations.Run.");
        }

        // A custom dialect replaces the engine entirely, so the enum is not consulted in that case.
        if (Dialect is null && !Enum.IsDefined(Engine))
        {
            throw new InvalidOperationException(
                $"SequenceOptions.Engine is not a supported engine ({(int)Engine}). Themia sequences "
                + "support PostgreSQL, MySQL 8.0.13+ and SQL Server. Set SequenceOptions.Dialect to run "
                + "against another engine.");
        }
    }
}
```

`ISequenceProvider.cs`:

```csharp
namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Atomic numeric sequence allocator. Each call returns a value no other concurrent caller can receive
/// for the same tenant and key.
/// </summary>
/// <remarks>
/// <para>
/// Allocation runs in its OWN transaction and survives the calling transaction's rollback. That is the
/// intended semantic: gaps in the allocated range are normal — a rolled-back caller produces one —
/// while duplicates are catastrophic. Invoice, order and document numbering is the canonical use.
/// </para>
/// <para>
/// It does NOT guarantee gapless numbering, and cannot: the value is allocated before the caller's own
/// transaction commits. A regulator requiring an unbroken run of numbers needs a different mechanism.
/// </para>
/// <para>
/// Values are <see cref="long"/>. Formatting (<c>INV-2026-00042</c>) is the caller's; the provider has
/// no opinion about prefixes, padding or when a counter resets.
/// </para>
/// </remarks>
public interface ISequenceProvider
{
    /// <summary>Allocates the next value for the CURRENT tenant.</summary>
    /// <param name="sequenceKey">Caller-defined key, conventionally colon-namespaced (<c>DocNo:Invoice:2026</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no ambient tenant, or the sequence has not been seeded, or it is exhausted.
    /// </exception>
    Task<long> NextAsync(string sequenceKey, CancellationToken ct = default);

    /// <summary>Allocates <paramref name="count"/> contiguous values for the CURRENT tenant, ascending.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="count">How many values to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated values in ascending order.</returns>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">No ambient tenant, not seeded, or exhausted.</exception>
    Task<IReadOnlyList<long>> NextRangeAsync(string sequenceKey, int count, CancellationToken ct = default);

    /// <summary>Idempotently seeds the sequence for the CURRENT tenant.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="startValue">First value <see cref="NextAsync"/> returns. Ignored if the row exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">There is no ambient tenant.</exception>
    Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);

    /// <summary>Allocates the next HOST-LEVEL value, outside any tenant.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <remarks>
    /// A separate method rather than a null-tenant fallback on <see cref="NextAsync"/>. Background work
    /// only has an ambient tenant if it opted in (<c>BackgroundTenantScope.Begin</c>), so a job that lost
    /// its scope would otherwise draw every tenant's numbers from one shared counter with nothing
    /// reporting it. Host-level allocation has to be asked for.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The sequence has not been seeded, or is exhausted.</exception>
    Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default);

    /// <summary>Allocates <paramref name="count"/> contiguous HOST-LEVEL values, ascending.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="count">How many values to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated values in ascending order.</returns>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">Not seeded, or exhausted.</exception>
    Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default);

    /// <summary>Idempotently seeds a HOST-LEVEL sequence.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="startValue">First value returned. Ignored if the row exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null or empty.</exception>
    Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);
}
```

`ISequenceDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Framework.Data.Sequences;

/// <summary>Per-engine SQL and connection factory for the sequence allocator.</summary>
/// <remarks>
/// Public so an adopter on an engine Themia does not ship can supply one without forking the package —
/// the same seam as <c>IExceptionalSqlDialect</c> and <c>INotificationsSqlDialect</c>. Every statement
/// takes <c>@tenant</c>, <c>@key</c> and (where it writes) <c>@val</c>.
/// </remarks>
public interface ISequenceDialect
{
    /// <summary>Opens a NEW connection. Enlistment in an ambient transaction must be suppressed.</summary>
    /// <param name="connectionString">The configured connection string.</param>
    /// <returns>An unopened connection.</returns>
    DbConnection CreateConnection(string connectionString);

    /// <summary>Reads <c>next_value</c> for <c>(@tenant, @key)</c>, holding a row lock until commit.</summary>
    string SelectForUpdateSql { get; }

    /// <summary>Sets <c>next_value = @val</c> for <c>(@tenant, @key)</c>.</summary>
    string UpdateNextValueSql { get; }

    /// <summary>Inserts <c>(@tenant, @key, @val)</c> atomically, doing nothing when the row exists.</summary>
    string InsertIfMissingSql { get; }
}
```

`PublicAPI.Shipped.txt`: empty file.
`PublicAPI.Unshipped.txt`: single line `#nullable enable`.

Test project `tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
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
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Sequences/Themia.Framework.Data.Sequences.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add both projects to the solution**

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework.Data.Sequences/Themia.Framework.Data.Sequences.csproj
dotnet sln Themia.sln add tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj
```

- [ ] **Step 5: Add the public API entries**

Append to `src/framework/Themia.Framework.Data.Sequences/PublicAPI.Unshipped.txt`, then sort the file:

```
Themia.Framework.Data.Sequences.ISequenceDialect
Themia.Framework.Data.Sequences.ISequenceDialect.CreateConnection(string! connectionString) -> System.Data.Common.DbConnection!
Themia.Framework.Data.Sequences.ISequenceDialect.InsertIfMissingSql.get -> string!
Themia.Framework.Data.Sequences.ISequenceDialect.SelectForUpdateSql.get -> string!
Themia.Framework.Data.Sequences.ISequenceDialect.UpdateNextValueSql.get -> string!
Themia.Framework.Data.Sequences.ISequenceProvider
Themia.Framework.Data.Sequences.ISequenceProvider.EnsureHostSequenceAsync(string! sequenceKey, long startValue = 1, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Themia.Framework.Data.Sequences.ISequenceProvider.EnsureSequenceAsync(string! sequenceKey, long startValue = 1, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task!
Themia.Framework.Data.Sequences.ISequenceProvider.NextAsync(string! sequenceKey, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<long>!
Themia.Framework.Data.Sequences.ISequenceProvider.NextHostAsync(string! sequenceKey, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<long>!
Themia.Framework.Data.Sequences.ISequenceProvider.NextHostRangeAsync(string! sequenceKey, int count, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<long>!>!
Themia.Framework.Data.Sequences.ISequenceProvider.NextRangeAsync(string! sequenceKey, int count, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<long>!>!
Themia.Framework.Data.Sequences.SequenceEngine
Themia.Framework.Data.Sequences.SequenceEngine.MySql = 1 -> Themia.Framework.Data.Sequences.SequenceEngine
Themia.Framework.Data.Sequences.SequenceEngine.Postgres = 0 -> Themia.Framework.Data.Sequences.SequenceEngine
Themia.Framework.Data.Sequences.SequenceEngine.SqlServer = 2 -> Themia.Framework.Data.Sequences.SequenceEngine
Themia.Framework.Data.Sequences.SequenceOptions
Themia.Framework.Data.Sequences.SequenceOptions.ConnectionString.get -> string!
Themia.Framework.Data.Sequences.SequenceOptions.ConnectionString.set -> void
Themia.Framework.Data.Sequences.SequenceOptions.Dialect.get -> Themia.Framework.Data.Sequences.ISequenceDialect?
Themia.Framework.Data.Sequences.SequenceOptions.Dialect.set -> void
Themia.Framework.Data.Sequences.SequenceOptions.Engine.get -> Themia.Framework.Data.Sequences.SequenceEngine
Themia.Framework.Data.Sequences.SequenceOptions.Engine.set -> void
Themia.Framework.Data.Sequences.SequenceOptions.SequenceOptions() -> void
Themia.Framework.Data.Sequences.SequenceOptions.Validate() -> void
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj`
Expected: PASS, 5 tests.

- [ ] **Step 7: Confirm a clean solution build**

Run: `dotnet build Themia.sln --no-incremental 2>&1 | grep -E "error|Build succeeded"`
Expected: `Build succeeded.` — no `RS0016`.

- [ ] **Step 8: Commit**

```bash
git add src/framework/Themia.Framework.Data.Sequences tests/Themia.Framework.Data.Sequences.Tests Themia.sln
git commit -m "feat(sequences): package scaffold, options and the allocator contract"
```

---

### Task 2: The three dialects

**Files:**
- Create: `src/framework/Themia.Framework.Data.Sequences/Dialects/PostgresSequenceDialect.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/Dialects/MySqlSequenceDialect.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/Dialects/SqlServerSequenceDialect.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/Dialects/SequenceDialectFactory.cs`
- Test: `tests/Themia.Framework.Data.Sequences.Tests/SequenceDialectTests.cs`

**Interfaces:**
- Consumes: `SequenceEngine` and `ISequenceDialect` from Task 1.
- Produces: three internal `ISequenceDialect` implementations and `SequenceDialectFactory.For(SequenceEngine)` returning `ISequenceDialect`.

Parameter names used by every dialect's SQL, and therefore by Task 3: `@tenant`, `@key`, `@val`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.Tests/SequenceDialectTests.cs`:

```csharp
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class SequenceDialectTests
{
    [Theory]
    [InlineData(SequenceEngine.Postgres, "Npgsql")]
    [InlineData(SequenceEngine.MySql, "MySqlConnector")]
    [InlineData(SequenceEngine.SqlServer, "Microsoft.Data.SqlClient")]
    public void Factory_ReturnsTheEngineSpecificDialect(SequenceEngine engine, string expectedConnectionNamespace)
    {
        var dialect = SequenceDialectFactory.For(engine);

        using var connection = dialect.CreateConnection(ConnectionStringFor(engine));
        Assert.StartsWith(expectedConnectionNamespace, connection.GetType().Namespace, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_RejectsAnUndefinedEngine()
        => Assert.Throws<NotSupportedException>(() => SequenceDialectFactory.For((SequenceEngine)99));

    // Every dialect locks the row it is about to advance. Without the lock two callers read the same
    // NextValue and both return it -- the duplicate this package exists to prevent.
    [Theory]
    [InlineData(SequenceEngine.Postgres, "FOR UPDATE")]
    [InlineData(SequenceEngine.MySql, "FOR UPDATE")]
    [InlineData(SequenceEngine.SqlServer, "UPDLOCK")]
    public void SelectForUpdate_TakesARowLock(SequenceEngine engine, string lockClause)
        => Assert.Contains(lockClause, SequenceDialectFactory.For(engine).SelectForUpdateSql, StringComparison.OrdinalIgnoreCase);

    // Seeding must be a single atomic statement. The naive "SELECT then INSERT" races: two callers both
    // see no row, both insert, the second gets a primary-key violation.
    [Theory]
    [InlineData(SequenceEngine.Postgres, "ON CONFLICT")]
    [InlineData(SequenceEngine.MySql, "INSERT IGNORE")]
    [InlineData(SequenceEngine.SqlServer, "WHERE NOT EXISTS")]
    public void InsertIfMissing_IsAtomic(SequenceEngine engine, string marker)
        => Assert.Contains(marker, SequenceDialectFactory.For(engine).InsertIfMissingSql, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(SequenceEngine.Postgres)]
    [InlineData(SequenceEngine.MySql)]
    [InlineData(SequenceEngine.SqlServer)]
    public void EveryStatement_KeysOnBothTenantAndSequenceKey(SequenceEngine engine)
    {
        // The primary key is (tenant_id, sequence_key). A statement that filtered on sequence_key alone
        // would read or advance another tenant's counter, which no test of a single tenant would catch.
        var dialect = SequenceDialectFactory.For(engine);

        foreach (var sql in new[] { dialect.SelectForUpdateSql, dialect.UpdateNextValueSql, dialect.InsertIfMissingSql })
        {
            Assert.Contains("@tenant", sql, StringComparison.Ordinal);
            Assert.Contains("@key", sql, StringComparison.Ordinal);
        }
    }

    private static string ConnectionStringFor(SequenceEngine engine) => engine switch
    {
        SequenceEngine.Postgres => "Host=localhost;Database=x;Username=u;Password=p",
        SequenceEngine.MySql => "Server=localhost;Database=x;Uid=u;Pwd=p",
        SequenceEngine.SqlServer => "Server=localhost;Database=x;User Id=u;Password=p;TrustServerCertificate=true",
        _ => throw new NotSupportedException(),
    };
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj --filter SequenceDialectTests`
Expected: FAIL — `ISequenceDialect` / `SequenceDialectFactory` do not exist.

- [ ] **Step 3: Write the dialects**

`Dialects/PostgresSequenceDialect.cs`:

```csharp
using System.Data.Common;

using Npgsql;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>PostgreSQL dialect for the sequence allocator.</summary>
internal sealed class PostgresSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        // Enlist=false: the allocation must not join a caller's System.Transactions scope, or a rollback
        // there would take the allocated number back and it would be reissued to the next caller.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WHERE tenant_id = @tenant AND sequence_key = @key FOR UPDATE";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string InsertIfMissingSql =>
        "INSERT INTO themia_sequences (tenant_id, sequence_key, next_value) VALUES (@tenant, @key, @val) "
        + "ON CONFLICT (tenant_id, sequence_key) DO NOTHING";
}
```

`Dialects/MySqlSequenceDialect.cs`:

```csharp
using System.Data.Common;

using MySqlConnector;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>MySQL 8.0.13+ dialect for the sequence allocator. MariaDB is not supported.</summary>
internal sealed class MySqlSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        // AutoEnlist=false, NOT UseXaTransactions=false. UseXaTransactions only picks the MECHANISM
        // (XA versus local) MySqlConnector uses once it has already enlisted; AutoEnlist is what stops it
        // enlisting at all, and it defaults to true. Same reason as the other two dialects: joining a
        // caller's ambient System.Transactions scope would let their rollback take the allocated number
        // back, and the next caller would be handed it again.
        var builder = new MySqlConnectionStringBuilder(connectionString) { AutoEnlist = false };
        return new MySqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WHERE tenant_id = @tenant AND sequence_key = @key FOR UPDATE";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string InsertIfMissingSql =>
        "INSERT IGNORE INTO themia_sequences (tenant_id, sequence_key, next_value) VALUES (@tenant, @key, @val)";
}
```

`Dialects/SqlServerSequenceDialect.cs`:

```csharp
using System.Data.Common;

using Microsoft.Data.SqlClient;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>SQL Server dialect for the sequence allocator.</summary>
internal sealed class SqlServerSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Enlist = false };
        return new SqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WITH (UPDLOCK, HOLDLOCK) "
        + "WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    /// <remarks>
    /// INSERT ... SELECT ... WHERE NOT EXISTS with UPDLOCK/HOLDLOCK on the existence check, not
    /// "IF NOT EXISTS then INSERT" — the latter is two statements and races, and not MERGE, which has
    /// documented concurrency bugs across SQL Server versions.
    /// </remarks>
    public string InsertIfMissingSql =>
        "INSERT INTO themia_sequences (tenant_id, sequence_key, next_value) "
        + "SELECT @tenant, @key, @val WHERE NOT EXISTS ("
        + "SELECT 1 FROM themia_sequences WITH (UPDLOCK, HOLDLOCK) "
        + "WHERE tenant_id = @tenant AND sequence_key = @key)";
}
```

`Dialects/SequenceDialectFactory.cs`:

```csharp
namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>Resolves the <see cref="ISequenceDialect"/> for a <see cref="SequenceEngine"/>.</summary>
public static class SequenceDialectFactory
{
    /// <summary>Returns the dialect for <paramref name="engine"/>.</summary>
    /// <param name="engine">The configured engine.</param>
    /// <returns>The dialect.</returns>
    /// <exception cref="NotSupportedException"><paramref name="engine"/> is not a supported engine.</exception>
    public static ISequenceDialect For(SequenceEngine engine) => engine switch
    {
        SequenceEngine.Postgres => new PostgresSequenceDialect(),
        SequenceEngine.MySql => new MySqlSequenceDialect(),
        SequenceEngine.SqlServer => new SqlServerSequenceDialect(),

        // Exhaustive on purpose: a new engine must break this build rather than fall into a default and
        // silently allocate against the wrong SQL.
        _ => throw new NotSupportedException(
            $"Themia sequences do not support engine '{engine}'. Supported: PostgreSQL, MySQL 8.0.13+, "
            + "SQL Server. Supply a custom ISequenceDialect for anything else."),
    };
}
```

- [ ] **Step 4: Add the public API entries**

Append to `PublicAPI.Unshipped.txt` and sort:

```
Themia.Framework.Data.Sequences.Dialects.SequenceDialectFactory
static Themia.Framework.Data.Sequences.Dialects.SequenceDialectFactory.For(Themia.Framework.Data.Sequences.SequenceEngine engine) -> Themia.Framework.Data.Sequences.ISequenceDialect!
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj`
Expected: PASS, 17 tests.

- [ ] **Step 6: Commit**

```bash
git add src/framework/Themia.Framework.Data.Sequences tests/Themia.Framework.Data.Sequences.Tests
git commit -m "feat(sequences): per-engine dialects with row locking and atomic seeding"
```

---

### Task 3: The migration

**Files:**
- Create: `src/framework/Themia.Framework.Data.Sequences/Migrations/SequencesSchemaMigration.cs`
- Create: `tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj`
- Create: `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequencesSchemaMigrationTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `SequencesSchemaMigration`, migration id `202609050001`, creating table `themia_sequences`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequencesSchemaMigrationTests.cs`:

```csharp
using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SequencesSchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Migration_CreatesTheTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        var columns = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'themia_sequences'")).ToList();

        Assert.Contains("tenant_id", columns);
        Assert.Contains("sequence_key", columns);
        Assert.Contains("next_value", columns);
    }

    [Fact]
    public async Task Migration_IsReplaySafe()
    {
        // The per-assembly version ledger (coord #0078) starts EMPTY on every database that predates it,
        // so Up() runs against objects already there. Coord #0085 and #0096 are the outages this prevents:
        // an unguarded CREATE crash-loops the host at boot.
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(SequencesSchemaMigration).Assembly);

        await using (var conn = new NpgsqlConnection(connString))
        {
            await conn.ExecuteAsync("DELETE FROM themia_version_themia_framework_data_sequences");
        }

        // Must not throw: the table is already there and the ledger no longer remembers creating it.
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(SequencesSchemaMigration).Assembly);
    }

    [Fact]
    public async Task TenantId_IsNotNullable_SoItCanBePartOfThePrimaryKey()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        var isNullable = await conn.ExecuteScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns "
            + "WHERE table_name = 'themia_sequences' AND column_name = 'tenant_id'");

        Assert.Equal("NO", isNullable);
    }
}
```

- [ ] **Step 2: Create the integration test project and add both to the solution**

`tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
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
    <PackageReference Include="Dapper" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.MySql" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="MySqlConnector" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Sequences/Themia.Framework.Data.Sequences.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln Themia.sln add tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj`
Expected: FAIL — `SequencesSchemaMigration` does not exist.

- [ ] **Step 4: Write the migration**

`src/framework/Themia.Framework.Data.Sequences/Migrations/SequencesSchemaMigration.cs`:

```csharp
using FluentMigrator;

namespace Themia.Framework.Data.Sequences.Migrations;

/// <summary>Creates <c>themia_sequences</c>, the counter table behind <see cref="ISequenceProvider"/>.</summary>
/// <remarks>
/// Unqualified (default schema), matching the other framework-level tables — <c>themia_version_*</c> and
/// <c>data_protection_keys</c> — so a consumer with a non-default <c>search_path</c> does not end up with
/// schema and runtime pointing at different places (coord #0088).
/// </remarks>
[Migration(202609050001, "Themia.Sequences: create themia_sequences")]
public sealed class SequencesSchemaMigration : Migration
{
    private const string TableName = "themia_sequences";

    /// <inheritdoc />
    public override void Up()
    {
        // Adopt-if-exists, per coord #0078/#0085/#0096: the per-assembly version ledger starts empty on
        // every database that predates it, so this runs once against a table that may already be there.
        // An unguarded CREATE fails and crash-loops the host at boot.
        if (Schema.Table(TableName).Exists()) return;

        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(() =>
            Create.Table(TableName)
                // NOT NULL with '' for host-level: TenantId is nullable throughout Themia, but no engine
                // permits a NULL column in a primary key. The alternative -- a surrogate key plus UNIQUE
                // over a nullable column -- has engine-divergent NULL semantics (PostgreSQL admits many
                // NULL rows, SQL Server one), which would silently allow two host-level rows for one key
                // and therefore two allocators.
                .WithColumn("tenant_id").AsString(100).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn("sequence_key").AsString(100).NotNullable()
                .WithColumn("next_value").AsInt64().NotNullable());

        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(() =>
            Create.PrimaryKey("pk_themia_sequences").OnTable(TableName)
                .Columns("tenant_id", "sequence_key"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia sequences support only PostgreSQL, MySQL and SQL Server. The active database "
                + "provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table(TableName);
}
```

- [ ] **Step 5: Add the public API entries**

```
Themia.Framework.Data.Sequences.Migrations.SequencesSchemaMigration
Themia.Framework.Data.Sequences.Migrations.SequencesSchemaMigration.SequencesSchemaMigration() -> void
override Themia.Framework.Data.Sequences.Migrations.SequencesSchemaMigration.Down() -> void
override Themia.Framework.Data.Sequences.Migrations.SequencesSchemaMigration.Up() -> void
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj`
Expected: PASS, 3 tests.

- [ ] **Step 7: Enrol the package in the cross-engine replay registry**

`tests/Themia.Migrations.ReplayTests/MigrationReplayTests.cs` keeps an **exhaustive** `AssemblyNames`
list, and its own comment says why: *"A migrating package missing from it is a package whose upgrade
nobody tested — which is exactly how the collision this all came from went unnoticed."* That registry is
the ONLY place replay-safety runs on SQL Server as well as PostgreSQL; this task's own tests are Postgres
only, so without this the adopt-if-exists guard is unverified on two of the three supported engines.

Add `"Themia.Framework.Data.Sequences"` to `AssemblyNames`, and add the matching `ProjectReference` to
`tests/Themia.Migrations.ReplayTests/Themia.Migrations.ReplayTests.csproj` (its comment: *"a new
migrating package that is not here is untested for replay"*).

Run: `dotnet test tests/Themia.Migrations.ReplayTests/Themia.Migrations.ReplayTests.csproj`
Expected: PASS, including `Applying_twice_is_safe` and `Each_assembly_records_in_its_own_ledger` for the
new assembly on both engines.

- [ ] **Step 8: Commit**

```bash
git add src/framework/Themia.Framework.Data.Sequences tests/Themia.Framework.Data.Sequences.IntegrationTests tests/Themia.Migrations.ReplayTests Themia.sln
git commit -m "feat(sequences): FluentMigrator schema, adopt-if-exists, composite primary key"
```

---

### Task 4: The allocator — seeding and tenant-scoped allocation

**Files:**
- Create: `src/framework/Themia.Framework.Data.Sequences/SequenceProvider.cs`
- Test: `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceProviderTests.cs`

**Interfaces:**
- Consumes: `ISequenceDialect`, `SequenceDialectFactory.For`, `SequenceOptions`, `ISequenceProvider` (Tasks 1–2); `ITenantContext` and `TenantId` from `Themia.Framework.Core.Abstractions.Tenancy`.
- Produces: `internal sealed class SequenceProvider(SequenceOptions options, ITenantContext tenantContext) : ISequenceProvider`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceProviderTests.cs`:

```csharp
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SequenceProviderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private ISequenceProvider ProviderFor(string? tenant) =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = container.GetConnectionString(), Engine = SequenceEngine.Postgres },
            new TenantContext(tenant is null ? null : new TenantId(tenant)));

    [Fact]
    public async Task Next_ReturnsTheSeededStartValueThenAdvances()
    {
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync("DocNo:Invoice", startValue: 100);

        Assert.Equal(100, await sut.NextAsync("DocNo:Invoice"));
        Assert.Equal(101, await sut.NextAsync("DocNo:Invoice"));
    }

    [Fact]
    public async Task Ensure_IsIdempotentAndPreservesAnExistingCounter()
    {
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync("DocNo:Order", startValue: 500);
        Assert.Equal(500, await sut.NextAsync("DocNo:Order"));

        // A second seed with a different start must NOT reset the counter, or a redeploy would reissue
        // every number already handed out.
        await sut.EnsureSequenceAsync("DocNo:Order", startValue: 1);
        Assert.Equal(501, await sut.NextAsync("DocNo:Order"));
    }

    [Fact]
    public async Task Next_OnAnUnseededKey_Throws()
    {
        // Not "create it implicitly at 1": a typo in a sequence key would then silently become a brand-new
        // counter, and two spellings would hand out the same numbers.
        var sut = ProviderFor("acme");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.NextAsync("DocNo:NeverSeeded"));
        Assert.Contains("DocNo:NeverSeeded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenants_DoNotShareACounter()
    {
        await ProviderFor("acme").EnsureSequenceAsync("DocNo:Invoice", startValue: 1);
        await ProviderFor("globex").EnsureSequenceAsync("DocNo:Invoice", startValue: 1);

        Assert.Equal(1, await ProviderFor("acme").NextAsync("DocNo:Invoice"));
        Assert.Equal(2, await ProviderFor("acme").NextAsync("DocNo:Invoice"));
        Assert.Equal(1, await ProviderFor("globex").NextAsync("DocNo:Invoice"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Next_RejectsABlankKey(string? key)
        => await Assert.ThrowsAsync<ArgumentException>(() => ProviderFor("acme").NextAsync(key!));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj --filter SequenceProviderTests`
Expected: FAIL — `SequenceProvider` does not exist.

- [ ] **Step 3: Write the allocator**

`src/framework/Themia.Framework.Data.Sequences/SequenceProvider.cs`:

```csharp
using System.Data;
using System.Data.Common;

using Dapper;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences.Dialects;

namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Allocates sequence values on its OWN connection and transaction, so the value survives the calling
/// transaction's rollback.
/// </summary>
internal sealed class SequenceProvider : ISequenceProvider
{
    /// <summary>Host-level rows use the empty string. <c>TenantId</c>'s constructor rejects null and
    /// whitespace, so no real tenant can ever collide with it.</summary>
    private const string HostTenant = "";

    private readonly SequenceOptions options;
    private readonly ITenantContext tenantContext;
    private readonly ISequenceDialect dialect;

    public SequenceProvider(SequenceOptions options, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantContext);
        options.Validate();

        this.options = options;
        this.tenantContext = tenantContext;
        dialect = options.Dialect ?? SequenceDialectFactory.For(options.Engine);
    }

    /// <inheritdoc />
    public Task<long> NextAsync(string sequenceKey, CancellationToken ct = default) =>
        AllocateAsync(RequireTenant(sequenceKey), sequenceKey, count: 1, ct)
            .ContinueWithFirst();

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> NextRangeAsync(string sequenceKey, int count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(RequireTenant(sequenceKey), sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default) =>
        SeedAsync(RequireTenant(sequenceKey), sequenceKey, startValue, ct);

    /// <inheritdoc />
    public Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);
        return AllocateAsync(HostTenant, sequenceKey, count: 1, ct).ContinueWithFirst();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(HostTenant, sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);
        return SeedAsync(HostTenant, sequenceKey, startValue, ct);
    }

    /// <summary>
    /// Resolves the ambient tenant, refusing to fall back to the host row.
    /// </summary>
    /// <remarks>
    /// Background work only has an ambient tenant if it opted in (<c>BackgroundTenantScope.Begin</c>).
    /// Treating "no tenant" as host-level would let a job that lost its scope draw every tenant's invoice
    /// numbers from one shared counter, with nothing reporting it. Host allocation must be asked for.
    /// </remarks>
    private string RequireTenant(string sequenceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);

        return tenantContext.CurrentTenantId?.Value
            ?? throw new InvalidOperationException(
                $"Cannot allocate sequence '{sequenceKey}': there is no ambient tenant. Wrap the call in a "
                + "tenant scope (background jobs must use BackgroundTenantScope.Begin), or call the Host "
                + "overload if a host-level counter is what you meant.");
    }

    private async Task<IReadOnlyList<long>> AllocateAsync(
        string tenant, string sequenceKey, int count, CancellationToken ct)
    {
        // Its own connection and transaction. This is the package: the number must survive the caller's
        // rollback, and it cannot if it shares the caller's transaction.
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);

        var current = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            dialect.SelectForUpdateSql, new { tenant, key = sequenceKey }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

        if (current is null) throw NotSeeded(tenant, sequenceKey);

        var first = current.Value;

        // Overflow is a loud failure. Unchecked, `+ count` wraps to negative at long.MaxValue and the
        // wrapped values collide with real ones once the counter comes back round.
        long advanced;
        try
        {
            advanced = checked(first + count);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Sequence '{sequenceKey}' is exhausted: next_value ({first}) cannot advance by {count} "
                + "without exceeding long.MaxValue.", ex);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.UpdateNextValueSql, new { tenant, key = sequenceKey, val = advanced }, tx,
            cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var allocated = new long[count];
        for (var i = 0; i < count; i++) allocated[i] = first + i;
        return allocated;
    }

    private async Task SeedAsync(string tenant, string sequenceKey, long startValue, CancellationToken ct)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertIfMissingSql, new { tenant, key = sequenceKey, val = startValue },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static InvalidOperationException NotSeeded(string tenant, string sequenceKey) =>
        new($"Sequence '{sequenceKey}' has not been seeded for "
            + $"{(tenant.Length == 0 ? "the host" : $"tenant '{tenant}'")}. "
            + "Call EnsureSequenceAsync (or EnsureHostSequenceAsync) first.");
}

/// <summary>Reduces a single-value allocation to its one element.</summary>
internal static class SequenceTaskExtensions
{
    public static async Task<long> ContinueWithFirst(this Task<IReadOnlyList<long>> task) =>
        (await task.ConfigureAwait(false))[0];
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj --filter SequenceProviderTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.Framework.Data.Sequences tests/Themia.Framework.Data.Sequences.IntegrationTests
git commit -m "feat(sequences): tenant-scoped allocation, idempotent seeding, overflow guard"
```

---

### Task 5: The missing-tenant refusal and the host-level API

**Files:**
- Test: `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceTenantScopeTests.cs`

**Interfaces:**
- Consumes: `SequenceProvider` from Task 4.
- Produces: nothing new — this task proves the behaviour the spec's blocker finding demanded.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceTenantScopeTests.cs`:

```csharp
using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The design's central safety property: a caller with no ambient tenant must FAIL, never fall through
/// to the host-level counter. A background job that lost its tenant scope would otherwise draw every
/// tenant's invoice numbers from one shared row, silently.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SequenceTenantScopeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private ISequenceProvider ProviderFor(string? tenant) =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = container.GetConnectionString(), Engine = SequenceEngine.Postgres },
            new TenantContext(tenant is null ? null : new TenantId(tenant)));

    [Fact]
    public async Task NextAsync_WithNoAmbientTenant_ThrowsAndAllocatesNothing()
    {
        await ProviderFor(null).EnsureHostSequenceAsync("DocNo:Invoice", startValue: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderFor(null).NextAsync("DocNo:Invoice"));

        // The message has to send the reader to the right layer, not just say "no tenant".
        Assert.Contains("BackgroundTenantScope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Host", ex.Message, StringComparison.Ordinal);

        // And nothing moved: the host counter is untouched, so the refusal is not a silent allocation.
        Assert.Equal(1, await ProviderFor(null).NextHostAsync("DocNo:Invoice"));
    }

    [Fact]
    public async Task EnsureSequenceAsync_WithNoAmbientTenant_Throws()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderFor(null).EnsureSequenceAsync("DocNo:Invoice"));

    [Fact]
    public async Task HostAndTenant_WithTheSameKey_AreDifferentCounters()
    {
        await ProviderFor(null).EnsureHostSequenceAsync("DocNo:Shared", startValue: 1);
        await ProviderFor("acme").EnsureSequenceAsync("DocNo:Shared", startValue: 1);

        Assert.Equal(1, await ProviderFor(null).NextHostAsync("DocNo:Shared"));
        Assert.Equal(2, await ProviderFor(null).NextHostAsync("DocNo:Shared"));
        Assert.Equal(1, await ProviderFor("acme").NextAsync("DocNo:Shared"));
    }

    [Fact]
    public async Task TheHostRowIsStoredAsAnEmptyTenantId()
    {
        await ProviderFor(null).EnsureHostSequenceAsync("DocNo:HostOnly", startValue: 7);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        var tenantId = await conn.ExecuteScalarAsync<string>(
            "SELECT tenant_id FROM themia_sequences WHERE sequence_key = 'DocNo:HostOnly'");

        // '' and not NULL: the primary key cannot hold NULL, and TenantId's constructor rejects
        // whitespace, so no real tenant can collide with this row.
        Assert.Equal(string.Empty, tenantId);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj --filter SequenceTenantScopeTests`
Expected: PASS, 4 tests. (Task 4 already implemented the behaviour; these tests pin it. If any fails, fix `SequenceProvider.RequireTenant`.)

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Framework.Data.Sequences.IntegrationTests
git commit -m "test(sequences): a missing ambient tenant fails instead of using the host counter"
```

---

### Task 6: Transaction independence — with a test that can fail

**Files:**
- Test: `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceTransactionIndependenceTests.cs`

**Interfaces:**
- Consumes: `SequenceProvider`, `SequenceOptions`, `SequenceEngine` from Tasks 1–4.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceTransactionIndependenceTests.cs`:

```csharp
using System.Transactions;

using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

using TransactionScope = System.Transactions.TransactionScope;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The defining semantic: an allocated number survives the caller's rollback.
/// </summary>
/// <remarks>
/// The obvious test — "allocate inside an outer transaction, roll it back, assert the number was not
/// reissued" — passes no matter what the implementation does, because the provider holds its own
/// connection and a rollback on a different connection cannot touch a committed row. It would stay green
/// against an implementation that had lost the semantic entirely. So this file pins the MECHANISM: first
/// it proves the check can go red, then it asserts the real behaviour.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SequenceTransactionIndependenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private ISequenceProvider Provider() =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = container.GetConnectionString(), Engine = SequenceEngine.Postgres },
            new TenantContext(new TenantId("acme")));

    // CONTROL. Allocation done the WRONG way -- on the caller's own connection and transaction -- is
    // undone by the rollback. This is what a broken implementation looks like, and it proves the
    // assertion below can actually fail.
    [Fact]
    public async Task Control_AllocatingOnTheCallersTransaction_LosesTheNumberOnRollback()
    {
        await Provider().EnsureSequenceAsync("DocNo:Control", startValue: 1);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE themia_sequences SET next_value = next_value + 1 "
                + "WHERE tenant_id = 'acme' AND sequence_key = 'DocNo:Control'", transaction: tx);
            await tx.RollbackAsync();
        }

        // Rolled back, so the counter never moved.
        Assert.Equal(1, await Provider().NextAsync("DocNo:Control"));
    }

    [Fact]
    public async Task AllocationSurvivesTheCallersRollback()
    {
        await Provider().EnsureSequenceAsync("DocNo:Survives", startValue: 1);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        long allocated;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            allocated = await Provider().NextAsync("DocNo:Survives");
            await tx.RollbackAsync();
        }

        Assert.Equal(1, allocated);

        // The number is gone for good -- a gap, which is the documented and intended outcome.
        Assert.Equal(2, await Provider().NextAsync("DocNo:Survives"));
    }

    [Fact]
    public async Task AllocationSurvivesAnAmbientSystemTransactionsScope()
    {
        // ADO providers default to Enlist=true, so a connection opened inside a TransactionScope would
        // join it and the allocation would roll back with the scope -- reissuing the number to the next
        // caller, silently. The dialects suppress enlistment; this pins it.
        await Provider().EnsureSequenceAsync("DocNo:Ambient", startValue: 1);

        long allocated;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            allocated = await Provider().NextAsync("DocNo:Ambient");
            // scope disposed without Complete() -> rollback
        }

        Assert.Equal(1, allocated);
        Assert.Equal(2, await Provider().NextAsync("DocNo:Ambient"));
    }
}
```

- [ ] **Step 2: Run test to verify the control fails first if enlistment is not suppressed**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj --filter SequenceTransactionIndependenceTests`
Expected: PASS, 3 tests. If `AllocationSurvivesAnAmbientSystemTransactionsScope` fails, the dialect is not suppressing enlistment — fix `CreateConnection` in the dialect (Task 2), do not weaken the test.

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Framework.Data.Sequences.IntegrationTests
git commit -m "test(sequences): pin transaction independence, with a control that proves the check can fail"
```

---

### Task 7: Concurrency and overflow, on all three engines

**Files:**
- Test: `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceConcurrencyTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.IntegrationTests/SequenceConcurrencyTests.cs`:

```csharp
using Dapper;

using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The whole claim of this package, on every engine it supports: no two callers ever receive the same
/// value. Locking SQL is per-engine and hand-written, so this has to run per-engine.
/// </summary>
public abstract class SequenceConcurrencyTests
{
    protected abstract string ConnString { get; }
    protected abstract SequenceEngine Engine { get; }

    private ISequenceProvider Provider() =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = ConnString, Engine = Engine },
            new TenantContext(new TenantId("acme")));

    [Fact]
    public async Task Fifty_ConcurrentAllocations_AreAllDistinct()
    {
        await Provider().EnsureSequenceAsync("DocNo:Concurrent", startValue: 1);

        var values = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Provider().NextAsync("DocNo:Concurrent")));

        Assert.Equal(50, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i).OrderBy(x => x), values.OrderBy(x => x));
    }

    [Fact]
    public async Task NextRange_ReturnsContiguousValuesAndAdvancesByCount()
    {
        var sut = Provider();
        await sut.EnsureSequenceAsync("DocNo:Range", startValue: 10);

        var batch = await sut.NextRangeAsync("DocNo:Range", 5);

        Assert.Equal([10L, 11L, 12L, 13L, 14L], batch);
        Assert.Equal(15, await sut.NextAsync("DocNo:Range"));
    }

    [Fact]
    public async Task NextRange_RejectsANonPositiveCount()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Provider().NextRangeAsync("DocNo:Range", 0));

    [Fact]
    public async Task AnExhaustedSequence_ThrowsInsteadOfWrappingNegative()
    {
        var sut = Provider();
        await sut.EnsureSequenceAsync("DocNo:Exhausted", startValue: long.MaxValue);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.NextAsync("DocNo:Exhausted"));
        Assert.Contains("exhausted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.Postgres;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class MySqlSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.MySql;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.MySql, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class SqlServerSequenceConcurrencyTests : SequenceConcurrencyTests, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnString => container.GetConnectionString();
    protected override SequenceEngine Engine => SequenceEngine.SqlServer;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, ConnString, typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
```

- [ ] **Step 2: Run test to verify it fails, then passes**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.IntegrationTests/Themia.Framework.Data.Sequences.IntegrationTests.csproj --filter SequenceConcurrencyTests`
Expected: PASS, 12 tests (4 × 3 engines).

If the concurrency test fails on one engine only, the bug is in that dialect's `SelectForUpdateSql` — the
row lock is missing or not held to commit. Do not add retries to make it pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Framework.Data.Sequences.IntegrationTests
git commit -m "test(sequences): concurrency, ranges and overflow on Postgres, MySQL and SQL Server"
```

---

### Task 8: Registration, docs and release

**Files:**
- Create: `src/framework/Themia.Framework.Data.Sequences/DependencyInjection/SequenceServiceCollectionExtensions.cs`
- Create: `src/framework/Themia.Framework.Data.Sequences/README.md`
- Test: `tests/Themia.Framework.Data.Sequences.Tests/AddThemiaSequencesTests.cs`
- Modify: `CHANGELOG.md`
- Modify: `Directory.Build.props`
- Modify: `docs/themia-architecture-overview.md`

**Interfaces:**
- Consumes: `SequenceOptions`, `ISequenceProvider`, `SequenceProvider`.
- Produces: `IServiceCollection AddThemiaSequences(this IServiceCollection, Action<SequenceOptions>)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Sequences.Tests/AddThemiaSequencesTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using Themia.Framework.Data.Sequences;

using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class AddThemiaSequencesTests
{
    [Fact]
    public void AddThemiaSequences_RegistersTheProvider()
    {
        var services = new ServiceCollection();

        services.AddThemiaSequences(o =>
        {
            o.ConnectionString = "Host=localhost;Database=x;Username=u;Password=p";
            o.Engine = SequenceEngine.Postgres;
        });

        Assert.Contains(services, d => d.ServiceType == typeof(ISequenceProvider));
    }

    [Fact]
    public void AddThemiaSequences_ValidatesEagerly_SoAMisconfigurationFailsAtStartup()
    {
        // Not at the first allocation: a connection string typo should stop the deploy, not surface as a
        // failed invoice hours later.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaSequences(o => o.Engine = SequenceEngine.Postgres));

        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaSequences_RejectsANullConfigureCallback()
        => Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddThemiaSequences(null!));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.Sequences.Tests/Themia.Framework.Data.Sequences.Tests.csproj --filter AddThemiaSequencesTests`
Expected: FAIL — `AddThemiaSequences` does not exist.

- [ ] **Step 3: Write the registration**

`src/framework/Themia.Framework.Data.Sequences/DependencyInjection/SequenceServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Framework.Data.Sequences;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Themia sequence allocator.</summary>
public static class SequenceServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ISequenceProvider"/> to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the connection string and engine.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The resulting options are not usable.</exception>
    /// <remarks>
    /// Options are validated HERE rather than at the first allocation, so a connection-string typo stops
    /// the deployment instead of surfacing as a failed invoice hours later.
    /// <para>
    /// Scoped, because it reads the ambient <c>ITenantContext</c>. The provider holds no connection
    /// between calls — it opens one per allocation, by design.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddThemiaSequences(
        this IServiceCollection services, Action<SequenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SequenceOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddScoped<ISequenceProvider, SequenceProvider>();
        return services;
    }
}
```

- [ ] **Step 4: Add the public API entries**

```
Microsoft.Extensions.DependencyInjection.SequenceServiceCollectionExtensions
static Microsoft.Extensions.DependencyInjection.SequenceServiceCollectionExtensions.AddThemiaSequences(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Themia.Framework.Data.Sequences.SequenceOptions!>! configure) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 5: Write the package README**

`src/framework/Themia.Framework.Data.Sequences/README.md`:

````markdown
# Themia.Framework.Data.Sequences

Atomic, tenant-scoped document numbering. Allocation runs in its **own** transaction, so the number
survives the caller's rollback: **gaps are normal, duplicates are not.**

```csharp
services.AddThemiaSequences(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("Default")!;
    o.Engine           = SequenceEngine.Postgres;
});

await sequences.EnsureSequenceAsync("DocNo:Invoice:2026", startValue: 1);
var number = await sequences.NextAsync("DocNo:Invoice:2026");   // 1, then 2, …
```

Pass this assembly to `ThemiaMigrations.Run` so the `themia_sequences` table is created.

## Two things to know before adopting

**It does not guarantee gapless numbering, and cannot.** The value is allocated before your transaction
commits; if you roll back, the number is spent. If a regulator requires an unbroken run, this is not the
mechanism.

**There is no null-tenant fallback.** `NextAsync` throws when there is no ambient tenant. Background jobs
must establish one (`BackgroundTenantScope.Begin`), or call `NextHostAsync` when a host-level counter is
genuinely what you want. This is deliberate: a job that lost its tenant scope would otherwise draw every
tenant's numbers from one shared counter with nothing reporting it.

Formatting (`INV-2026-00042`) is yours. The provider returns a `long`.
````

Add to the csproj `PropertyGroup`:

```xml
<PackageReadmeFile>README.md</PackageReadmeFile>
```

and an item:

```xml
<ItemGroup>
  <None Include="README.md" Pack="true" PackagePath="/" />
</ItemGroup>
```

- [ ] **Step 6: Update the architecture overview**

In `docs/themia-architecture-overview.md`, change the Phase 3 line so Sequences is no longer pending:

```
- **Phase 3 — Advanced:** Geo, AI, Audit; Sequences EF-port ✅ (shipped as
  `Themia.Framework.Data.Sequences`, see `docs/superpowers/specs/2026-09-05-themia-sequences-design.md`
  — §F below is superseded on three points, recorded in that spec);
  SourceGenerator/analyzer merge.
```

- [ ] **Step 7: Bump the version and write the changelog**

In `Directory.Build.props`, change `<Version>0.21.4</Version>` to `<Version>0.22.0</Version>`.

Add to `CHANGELOG.md` directly under `## [Unreleased]`:

```markdown
## [0.22.0] - 2026-09-05

### Added
- **`Themia.Framework.Data.Sequences` — atomic, tenant-scoped document numbering.** Realises DECISION #2,
  porting the proven allocator from `Idevs.Net.CoreLib` and dropping its Serenity storage. Allocation runs
  in its **own** transaction so the number survives the caller's rollback: gaps are normal, duplicates are
  catastrophic. Both consumers need this for the PromptPay `BillRef1` running number and for Thai tax
  invoices, which are numbered sequentially by law (coord #0052, #0055).

  **One package, no engine split.** The allocator binds to no ORM — it needs a `DbConnection` and
  per-engine locking SQL — so unlike Identity and Challenges there is nothing to split along. Engine is
  chosen at runtime by an enum, following `Themia.Data.Migrations`.

  **There is no null-tenant fallback, and that is the point.** `NextAsync` throws when there is no ambient
  tenant; the host-level counter is reachable only through the explicit `Host` overloads. Background work
  only has a tenant if it opted in (`BackgroundTenantScope.Begin`), and invoice generation is the
  canonical scheduler job — mapping a missing tenant onto the host row would have let one lost scope draw
  every tenant's numbers from a single shared counter, with nothing reporting it. `NotificationOutboxDispatcher`
  already carries a forward-note about the identical shape.

  `tenant_id` is `NOT NULL` with `''` for host-level, a correction to §F: no engine permits a NULL column
  in a primary key, and the surrogate-key-plus-UNIQUE alternative has engine-divergent NULL semantics —
  PostgreSQL admits many NULL rows where SQL Server admits one, which would mean two allocators for one
  host sequence. `TenantId`'s constructor rejects whitespace, so no real tenant can collide with `''`.

  Does **not** guarantee gapless numbering and cannot: the value is allocated before the caller commits.
  Stated in the README because a regulator requiring an unbroken run needs a different mechanism.
```

- [ ] **Step 8: Full verification**

```bash
dotnet build Themia.sln --no-incremental 2>&1 | grep -E "error|warning RS|Build succeeded"
dotnet test Themia.sln
```

Expected: `Build succeeded.` with no `RS0016`, and every test assembly passing.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(sequences): DI registration, README, changelog and 0.22.0"
```

---

## Post-plan verification

Before opening the PR:

- [ ] **Mutation test with a canary.** Follow the harness convention from 0.21.3/0.21.4: the harness must
  first kill a mutant known to be lethal, and abort if it cannot. A harness that cannot report KILLED
  cannot report SURVIVED either. Mutants worth writing, at minimum:
  - `RequireTenant` returns `HostTenant` instead of throwing (the blocker this design exists to prevent)
  - `checked` becomes unchecked in `AllocateAsync`
  - `SelectForUpdateSql` drops its lock clause, per engine
  - the seed statement resets `next_value` instead of doing nothing when the row exists
  - `AllocateAsync` reuses an ambient connection instead of opening its own
  - `tenant` is dropped from the WHERE clause of `SelectForUpdateSql`, per engine
- [ ] Confirm the working tree is clean and no `.bak`/`.canary` files survive the mutation run.
- [ ] Open the PR describing the three §F corrections and the ambient-tenant decision.
