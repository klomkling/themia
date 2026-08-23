# Boot-time PostgreSQL schema probe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a Themia store whose table does not resolve through the connection's `search_path` fail at host startup instead of on a user's first request.

**Architecture:** A new neutral package `Themia.Data.Probes` runs one `to_regclass` query per table from an `IHostedService`, following the existing startup-advisory pattern (`Themia.Scheduling/UnclusteredPersistenceAdvisory.cs:31`). Five packages register it from their own DI extensions, supplying a connection factory so the probe package stays driver-free. Unresolvable → throw, host does not start. Resolvable but outside `public` while a `public` copy exists → warn. Connection failure → warn and skip.

**Tech Stack:** .NET 8 + .NET 10 (neutral package), xUnit, Testcontainers `postgres:16-alpine`, `Microsoft.Extensions.Hosting.Abstractions`, Npgsql (at call sites only).

**Spec:** `docs/superpowers/specs/2026-08-23-schema-agreement-design.md`

## Global Constraints

- New package targets `net8.0;net10.0`. The net8 leg is mandatory for neutral packages.
- `Version` is inherited from `Directory.Build.props`. Never set it in a csproj.
- Package versions come from `Directory.Packages.props` (central package management). `PackageReference` carries no `Version` attribute.
- `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` are inherited. Every public member needs an XML doc comment or the build fails with `RS0016`/`CS1591`.
- Every public API addition must be added to `PublicAPI.Unshipped.txt` or the build fails with `RS0016`.
- `System.Text.Json` only. Never `Newtonsoft.Json`.
- Log through `ILogger<T>`. No `Console.WriteLine`.
- Test projects live at `tests/<ProjectName>/` and are `net10.0` only.
- Never log or embed a connection string in an exception message.

---

### Task 1: `Themia.Data.Probes` package and the probe query

**Files:**
- Create: `src/neutral/Themia.Data.Probes/Themia.Data.Probes.csproj`
- Create: `src/neutral/Themia.Data.Probes/PublicAPI.Shipped.txt` (empty)
- Create: `src/neutral/Themia.Data.Probes/PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Data.Probes/SchemaVisibilityException.cs`
- Create: `src/neutral/Themia.Data.Probes/PostgresSchemaProbe.cs`
- Create: `tests/Themia.Data.Probes.IntegrationTests/Themia.Data.Probes.IntegrationTests.csproj`
- Create: `tests/Themia.Data.Probes.IntegrationTests/PostgresSchemaProbeTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `Themia.Data.Probes.PostgresSchemaProbe.Probe(IDbConnection connection, string tableName)` returning `ProbeResult`; `readonly record struct ProbeResult(string? ResolvedSchema, bool PublicCopyExists)`; `sealed class SchemaVisibilityException : Exception` with `SchemaVisibilityException(string message)`.

- [ ] **Step 1: Create the project files**

`src/neutral/Themia.Data.Probes/Themia.Data.Probes.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Data.Probes</PackageId>
    <Description>Boot-time database probes: confirms a table a Themia store addresses without a schema actually resolves through the connection's search_path.</Description>
    <!-- Version is inherited from Directory.Build.props (shared). Do not set it here. -->
  </PropertyGroup>
  <ItemGroup>
    <!-- No database driver on purpose: the caller supplies the connection, so each store keeps
         using the driver it already has and this package stays engine-agnostic. -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Data.Probes.IntegrationTests" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

Create `PublicAPI.Shipped.txt` as an empty file. Create `PublicAPI.Unshipped.txt` with:

```
#nullable enable
Themia.Data.Probes.PostgresSchemaProbe
Themia.Data.Probes.ProbeResult
Themia.Data.Probes.ProbeResult.ProbeResult() -> void
Themia.Data.Probes.ProbeResult.ProbeResult(string? ResolvedSchema, bool PublicCopyExists) -> void
Themia.Data.Probes.ProbeResult.PublicCopyExists.get -> bool
Themia.Data.Probes.ProbeResult.PublicCopyExists.init -> void
Themia.Data.Probes.ProbeResult.ResolvedSchema.get -> string?
Themia.Data.Probes.ProbeResult.ResolvedSchema.init -> void
Themia.Data.Probes.SchemaVisibilityException
Themia.Data.Probes.SchemaVisibilityException.SchemaVisibilityException(string! message) -> void
static Themia.Data.Probes.PostgresSchemaProbe.Probe(System.Data.IDbConnection! connection, string! tableName) -> Themia.Data.Probes.ProbeResult
```

Check `Directory.Packages.props` for `Microsoft.Extensions.Hosting.Abstractions` (present, `10.0.9`), `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`. Add a `PackageVersion` line for any that is missing, matching the version already used elsewhere in the file.

Add the project to `Themia.sln`:

```bash
dotnet sln Themia.sln add src/neutral/Themia.Data.Probes/Themia.Data.Probes.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Themia.Data.Probes.IntegrationTests/Themia.Data.Probes.IntegrationTests.csproj`:

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
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Data.Probes/Themia.Data.Probes.csproj" />
  </ItemGroup>
</Project>
```

`tests/Themia.Data.Probes.IntegrationTests/PostgresSchemaProbeTests.cs`:

```csharp
using System.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Data.Probes.IntegrationTests;

public sealed class PostgresSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private NpgsqlConnection Open(string? searchPath = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        if (searchPath is not null)
        {
            builder.SearchPath = searchPath;
        }

        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private void Exec(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Probe_ShouldReportPublic_WhenTableLivesInPublic()
    {
        Exec("CREATE TABLE IF NOT EXISTS probe_public (id int)");

        using var connection = Open();
        var result = PostgresSchemaProbe.Probe(connection, "probe_public");

        Assert.Equal("public", result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldReportNullSchema_WhenTableDoesNotResolve()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS probe_missing_app");
        Exec("CREATE TABLE IF NOT EXISTS public.probe_missing (id int)");

        // search_path names only the app schema, so public.probe_missing is off the path.
        using var connection = Open(searchPath: "probe_missing_app");
        var result = PostgresSchemaProbe.Probe(connection, "probe_missing");

        Assert.Null(result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldReportBothCopies_WhenTableExistsInAppAndPublic()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS probe_both_app");
        Exec("CREATE TABLE IF NOT EXISTS probe_both_app.probe_both (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.probe_both (id int)");

        using var connection = Open(searchPath: "probe_both_app,public");
        var result = PostgresSchemaProbe.Probe(connection, "probe_both");

        Assert.Equal("probe_both_app", result.ResolvedSchema);
        Assert.True(result.PublicCopyExists);
    }

    [Fact]
    public void Probe_ShouldResolveQuotedIdentifier_WhenTableNameIsCaseSensitive()
    {
        Exec("CREATE TABLE IF NOT EXISTS public.\"ProbeQuoted\" (id int)");

        using var connection = Open();
        var result = PostgresSchemaProbe.Probe(connection, "\"ProbeQuoted\"");

        Assert.Equal("public", result.ResolvedSchema);
    }

    [Fact]
    public void Probe_ShouldNotResolve_WhenQuotedTableIsProbedUnquoted()
    {
        Exec("CREATE TABLE IF NOT EXISTS public.\"ProbeUnquoted\" (id int)");

        using var connection = Open();
        // Unquoted folds to lower case: ProbeUnquoted != probeunquoted.
        var result = PostgresSchemaProbe.Probe(connection, "ProbeUnquoted");

        Assert.Null(result.ResolvedSchema);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Themia.Data.Probes.IntegrationTests --filter PostgresSchemaProbeTests
```

Expected: build failure — `PostgresSchemaProbe` and `ProbeResult` do not exist.

- [ ] **Step 4: Write the implementation**

`src/neutral/Themia.Data.Probes/SchemaVisibilityException.cs`:

```csharp
namespace Themia.Data.Probes;

/// <summary>
/// Thrown when a table a Themia store addresses without a schema does not resolve through the
/// connection's <c>search_path</c>.
/// </summary>
public sealed class SchemaVisibilityException : Exception
{
    /// <summary>Creates the exception with a diagnostic message.</summary>
    /// <param name="message">Message naming the component, the identifier and the remedy.</param>
    public SchemaVisibilityException(string message) : base(message)
    {
    }
}
```

`src/neutral/Themia.Data.Probes/PostgresSchemaProbe.cs`:

```csharp
using System.Data;

namespace Themia.Data.Probes;

/// <summary>Outcome of probing one table.</summary>
/// <param name="ResolvedSchema">
/// Schema the identifier resolves to through <c>search_path</c>, or <see langword="null"/> when it
/// resolves to nothing.
/// </param>
/// <param name="PublicCopyExists">Whether a table of the same name also exists in <c>public</c>.</param>
public readonly record struct ProbeResult(string? ResolvedSchema, bool PublicCopyExists);

/// <summary>
/// Confirms that a table a Themia store addresses without a schema actually resolves through the
/// connection's <c>search_path</c>. PostgreSQL only.
/// </summary>
public static class PostgresSchemaProbe
{
    // to_regclass returns NULL rather than throwing for an unresolvable name, and resolves names
    // exactly the way the store's own unqualified SQL does -- which is what makes it the right
    // probe rather than a lookup in information_schema.
    private const string Sql = """
        SELECT
          (SELECT n.nspname
             FROM pg_class c
             JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.oid = to_regclass(@name))            AS resolved_schema,
          (to_regclass('public.' || @name) IS NOT NULL)  AS public_copy_exists
        """;

    /// <summary>Probes one table on an open connection.</summary>
    /// <param name="connection">An open PostgreSQL connection.</param>
    /// <param name="tableName">
    /// The identifier exactly as the store's own SQL writes it -- unqualified, quoting included:
    /// <c>data_protection_keys</c>, but <c>"Exceptions"</c>. Every call site passes a compile-time
    /// constant, which is what makes the <c>'public.' || @name</c> concatenation safe.
    /// </param>
    /// <returns>The resolved schema and whether a <c>public</c> copy exists.</returns>
    public static ProbeResult Probe(IDbConnection connection, string tableName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.DbType = DbType.String;
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ProbeResult(null, false);
        }

        var schema = reader.IsDBNull(0) ? null : reader.GetString(0);
        var publicCopy = !reader.IsDBNull(1) && reader.GetBoolean(1);
        return new ProbeResult(schema, publicCopy);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Themia.Data.Probes.IntegrationTests --filter PostgresSchemaProbeTests
```

Expected: 5 passed.

- [ ] **Step 6: Clean build to surface PublicAPI diagnostics**

```bash
dotnet build Themia.sln --no-incremental
```

Expected: no `RS0016`. If any appears, the reported symbol is missing from `PublicAPI.Unshipped.txt` — add exactly the line the diagnostic names.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Data.Probes tests/Themia.Data.Probes.IntegrationTests Themia.sln Directory.Packages.props
git commit -m "feat(probes): add PostgresSchemaProbe resolving a table through search_path"
```

---

### Task 2: Boot-time hosted service and DI registration

**Files:**
- Create: `src/neutral/Themia.Data.Probes/PostgresSchemaProbeRegistration.cs`
- Create: `src/neutral/Themia.Data.Probes/PostgresSchemaProbeHostedService.cs`
- Create: `src/neutral/Themia.Data.Probes/PostgresSchemaProbeServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Data.Probes/PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Data.Probes.IntegrationTests/PostgresSchemaProbeHostedServiceTests.cs`

**Interfaces:**
- Consumes: `PostgresSchemaProbe.Probe`, `ProbeResult`, `SchemaVisibilityException` from Task 1.
- Produces: `PostgresSchemaProbeServiceCollectionExtensions.AddPostgresSchemaProbe(this IServiceCollection services, string componentName, Func<IServiceProvider, IDbConnection> connectionFactory, string[] tables, Func<IServiceProvider, bool>? appliesTo = null) -> IServiceCollection`. Tasks 3-7 call exactly this.

- [ ] **Step 1: Write the failing test**

`tests/Themia.Data.Probes.IntegrationTests/PostgresSchemaProbeHostedServiceTests.cs`:

```csharp
using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Data.Probes.IntegrationTests;

public sealed class PostgresSchemaProbeHostedServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private string ConnectionString(string? searchPath)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        if (searchPath is not null)
        {
            builder.SearchPath = searchPath;
        }

        return builder.ConnectionString;
    }

    private void Exec(string sql)
    {
        using var connection = new NpgsqlConnection(container.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IHost BuildHost(
        string connectionString,
        string[] tables,
        List<string> warnings,
        Func<IServiceProvider, bool>? appliesTo = null)
        => new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services => services.AddPostgresSchemaProbe(
                "Themia.Test",
                _ =>
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                },
                tables,
                appliesTo))
            .Build();

    [Fact]
    public async Task Host_ShouldStart_WhenTableResolvesOutsidePublic()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_app_only");
        Exec("CREATE TABLE IF NOT EXISTS hs_app_only.hs_only (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_app_only"), ["hs_only"], warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTableDoesNotResolve()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_missing_app");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_missing (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_missing_app"), ["hs_missing"], warnings);

        var ex = await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
        Assert.Contains("hs_missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ShouldWarn_WhenAStrayPublicCopyExists()
    {
        Exec("CREATE SCHEMA IF NOT EXISTS hs_both_app");
        Exec("CREATE TABLE IF NOT EXISTS hs_both_app.hs_both (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_both (id int)");

        var warnings = new List<string>();
        using var host = BuildHost(ConnectionString("hs_both_app,public"), ["hs_both"], warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(warnings);
        Assert.Contains("hs_both_app", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_ShouldStartAndWarn_WhenTheDatabaseIsUnreachable()
    {
        // A connection failure is an availability fault, not a configuration fault. Throwing here
        // would newly make host startup depend on database uptime.
        var warnings = new List<string>();
        using var host = BuildHost(
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
            ["anything"],
            warnings);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(warnings);
    }

    [Fact]
    public async Task Host_ShouldSkipTheProbe_WhenAppliesToIsFalse()
    {
        var warnings = new List<string>();
        using var host = BuildHost(
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
            ["anything"],
            warnings,
            appliesTo: _ => false);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Host_ShouldRunEveryRegistration_WhenTwoProbesAreRegistered()
    {
        // AddHostedService<T> de-duplicates by implementation type, which would silently collapse
        // the second registration. The extension must not use it.
        Exec("CREATE SCHEMA IF NOT EXISTS hs_two_app");
        Exec("CREATE TABLE IF NOT EXISTS hs_two_app.hs_two_a (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_two_a (id int)");
        Exec("CREATE TABLE IF NOT EXISTS hs_two_app.hs_two_b (id int)");
        Exec("CREATE TABLE IF NOT EXISTS public.hs_two_b (id int)");

        var warnings = new List<string>();
        var connectionString = ConnectionString("hs_two_app,public");

        using var host = new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(warnings));
            })
            .ConfigureServices(services =>
            {
                IDbConnection Factory(IServiceProvider _)
                {
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    return connection;
                }

                services.AddPostgresSchemaProbe("Themia.A", Factory, ["hs_two_a"]);
                services.AddPostgresSchemaProbe("Themia.B", Factory, ["hs_two_b"]);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(2, warnings.Count);
    }
}

internal sealed class CapturingLoggerProvider(List<string> warnings) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(warnings);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Themia.Data.Probes.IntegrationTests --filter PostgresSchemaProbeHostedServiceTests
```

Expected: build failure — `AddPostgresSchemaProbe` does not exist.

- [ ] **Step 3: Write the implementation**

`src/neutral/Themia.Data.Probes/PostgresSchemaProbeRegistration.cs`:

```csharp
using System.Data;

namespace Themia.Data.Probes;

/// <summary>One package's probe registration: what to open, what to check, and whether it applies.</summary>
internal sealed class PostgresSchemaProbeRegistration(
    string componentName,
    Func<IServiceProvider, IDbConnection> connectionFactory,
    IReadOnlyList<string> tables,
    Func<IServiceProvider, bool>? appliesTo)
{
    public string ComponentName { get; } = componentName;

    public Func<IServiceProvider, IDbConnection> ConnectionFactory { get; } = connectionFactory;

    public IReadOnlyList<string> Tables { get; } = tables;

    public Func<IServiceProvider, bool>? AppliesTo { get; } = appliesTo;
}
```

`src/neutral/Themia.Data.Probes/PostgresSchemaProbeHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Data.Probes;

/// <summary>
/// Runs the schema probe once at host startup. Follows the advisory pattern used by
/// Themia.Scheduling, with one difference: this one refuses rather than advises, so a table that
/// does not resolve stops the host instead of surfacing on a user's first request.
/// </summary>
internal sealed class PostgresSchemaProbeHostedService(
    IServiceProvider services,
    ILogger<PostgresSchemaProbeHostedService> logger,
    PostgresSchemaProbeRegistration registration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (registration.AppliesTo is not null && !registration.AppliesTo(services))
        {
            return Task.CompletedTask;
        }

        List<ProbeResult?> results;
        try
        {
            using var connection = registration.ConnectionFactory(services);
            results = registration.Tables
                .Select(table => (ProbeResult?)PostgresSchemaProbe.Probe(connection, table))
                .ToList();
        }
        catch (Exception ex)
        {
            // Availability, not configuration. Throwing here would newly couple host startup to
            // database uptime for consumers that do not migrate on boot.
            logger.LogWarning(
                ex,
                "{Component}: could not run the schema probe, so schema agreement was not verified. "
                + "This is not evidence of a schema problem.",
                registration.ComponentName);
            return Task.CompletedTask;
        }

        for (var i = 0; i < results.Count; i++)
        {
            var table = registration.Tables[i];
            var result = results[i]!.Value;

            if (result.ResolvedSchema is null)
            {
                throw new SchemaVisibilityException(
                    $"{registration.ComponentName}: table {table} does not resolve through this "
                    + $"connection's search_path"
                    + (result.PublicCopyExists
                        ? ", although a table of that name exists in 'public', which is where Themia's "
                          + "migrations create it. Put 'public' on the search_path, or point the "
                          + "connection at the schema that holds the table."
                        : " and no table of that name exists in 'public' either. Run the migrations, "
                          + "or point the connection at the schema that holds the table."));
            }

            if (!string.Equals(result.ResolvedSchema, "public", StringComparison.Ordinal)
                && result.PublicCopyExists)
            {
                logger.LogWarning(
                    "{Component}: this connection resolves {Table} in schema '{ResolvedSchema}', but a "
                    + "table of that name also exists in 'public', which is where Themia's migrations "
                    + "write. A later Themia migration would alter the copy this store does not read. "
                    + "The match is by name, so an unrelated table of the same name in 'public' "
                    + "produces this warning too.",
                    registration.ComponentName,
                    table,
                    result.ResolvedSchema);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`src/neutral/Themia.Data.Probes/PostgresSchemaProbeServiceCollectionExtensions.cs`:

```csharp
using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Data.Probes;

/// <summary>Registers the boot-time PostgreSQL schema probe.</summary>
public static class PostgresSchemaProbeServiceCollectionExtensions
{
    /// <summary>
    /// Verifies at host startup that every named table resolves through the connection's
    /// <c>search_path</c>. A table that does not resolve stops the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="componentName">Names the component in messages, for example <c>Themia.Exceptional</c>.</param>
    /// <param name="connectionFactory">Opens a short-lived connection for the probe.</param>
    /// <param name="tables">
    /// Identifiers exactly as the store's own SQL writes them -- unqualified, quoting included.
    /// </param>
    /// <param name="appliesTo">
    /// Optional predicate deciding whether the probe runs at all. Used by packages that serve more
    /// than one engine and only learn which one at runtime.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddPostgresSchemaProbe(
        this IServiceCollection services,
        string componentName,
        Func<IServiceProvider, IDbConnection> connectionFactory,
        string[] tables,
        Func<IServiceProvider, bool>? appliesTo = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(tables);

        var registration = new PostgresSchemaProbeRegistration(
            componentName, connectionFactory, tables, appliesTo);

        // Deliberately NOT AddHostedService<T>: it registers through TryAddEnumerable, which
        // de-duplicates by implementation type, so a second package's probe would be dropped.
        services.AddSingleton<IHostedService>(sp => new PostgresSchemaProbeHostedService(
            sp,
            sp.GetRequiredService<ILogger<PostgresSchemaProbeHostedService>>(),
            registration));

        return services;
    }
}
```

Append to `PublicAPI.Unshipped.txt`:

```
Themia.Data.Probes.PostgresSchemaProbeServiceCollectionExtensions
static Themia.Data.Probes.PostgresSchemaProbeServiceCollectionExtensions.AddPostgresSchemaProbe(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, string! componentName, System.Func<System.IServiceProvider!, System.Data.IDbConnection!>! connectionFactory, string![]! tables, System.Func<System.IServiceProvider!, bool>? appliesTo = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Themia.Data.Probes.IntegrationTests --filter PostgresSchemaProbeHostedServiceTests
```

Expected: 6 passed.

- [ ] **Step 5: Clean build**

```bash
dotnet build Themia.sln --no-incremental
```

Expected: no `RS0016`, no warnings.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Data.Probes tests/Themia.Data.Probes.IntegrationTests
git commit -m "feat(probes): fail host startup when a store's table does not resolve"
```

---

### Task 3: Wire `Themia.AspNetCore.DataProtection.PostgreSql`

**Files:**
- Modify: `src/neutral/Themia.AspNetCore.DataProtection.PostgreSql/Themia.AspNetCore.DataProtection.PostgreSql.csproj`
- Modify: `src/neutral/Themia.AspNetCore.DataProtection.PostgreSql/DataProtectionBuilderExtensions.cs:32`
- Create: `tests/Themia.AspNetCore.DataProtection.IntegrationTests/DataProtectionSchemaProbeTests.cs`

**Interfaces:**
- Consumes: `AddPostgresSchemaProbe` from Task 2.
- Produces: nothing new. `PersistKeysToThemiaPostgres(this IDataProtectionBuilder builder, string connectionString, bool runMigration = true, ThemiaMigrationOptions? migrationOptions = null)` keeps its signature.

- [ ] **Step 1: Write the failing test**

`tests/Themia.AspNetCore.DataProtection.IntegrationTests/DataProtectionSchemaProbeTests.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.AspNetCore.DataProtection.IntegrationTests;

public sealed class DataProtectionSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheKeyTableIsOffTheSearchPath()
    {
        // The migration creates public.data_protection_keys; the app then runs on a search_path
        // that does not include public. Today this surfaces on the first protector, not at boot.
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        var migrationConnectionString = builder.ConnectionString;

        using (var seed = new NpgsqlConnection(migrationConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS dp_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "dp_app";
        var appConnectionString = builder.ConnectionString;

        using var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddDataProtection()
                .SetApplicationName("probe-test")
                .PersistKeysToThemiaPostgres(appConnectionString, runMigration: false))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Themia.AspNetCore.DataProtection.IntegrationTests --filter DataProtectionSchemaProbeTests
```

Expected: FAIL — the host starts, no exception thrown.

- [ ] **Step 3: Add the project reference**

In `src/neutral/Themia.AspNetCore.DataProtection.PostgreSql/Themia.AspNetCore.DataProtection.PostgreSql.csproj`, inside the existing `ItemGroup` holding `ProjectReference` elements:

```xml
<ProjectReference Include="../Themia.Data.Probes/Themia.Data.Probes.csproj" />
```

In `tests/Themia.AspNetCore.DataProtection.IntegrationTests/Themia.AspNetCore.DataProtection.IntegrationTests.csproj`, add if not already present:

```xml
<PackageReference Include="Testcontainers.PostgreSql" />
```

- [ ] **Step 4: Register the probe**

In `DataProtectionBuilderExtensions.cs`, inside `PersistKeysToThemiaPostgres`, after the existing migration block and before the method returns `builder`:

```csharp
// Boot-time check: the migration writes public.data_protection_keys, but this store reads
// unqualified and follows search_path. A mismatch otherwise surfaces on the first protector,
// which is a user request, not startup.
builder.Services.AddPostgresSchemaProbe(
    "Themia.AspNetCore.DataProtection",
    _ =>
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    },
    ["data_protection_keys"]);
```

Add `using Themia.Data.Probes;` and `using Microsoft.Extensions.DependencyInjection;` to the file's usings if absent.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/Themia.AspNetCore.DataProtection.IntegrationTests --filter DataProtectionSchemaProbeTests
```

Expected: PASS.

- [ ] **Step 6: Run the package's whole suite for regressions**

```bash
dotnet test tests/Themia.AspNetCore.DataProtection.IntegrationTests
dotnet test tests/Themia.AspNetCore.DataProtection.Tests
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.AspNetCore.DataProtection.PostgreSql tests/Themia.AspNetCore.DataProtection.IntegrationTests
git commit -m "feat(dataprotection): probe data_protection_keys resolution at startup"
```

---

### Task 4: Wire `Themia.Exceptional.PostgreSql`

**Files:**
- Modify: `src/neutral/Themia.Exceptional.PostgreSql/Themia.Exceptional.PostgreSql.csproj`
- Modify: `src/neutral/Themia.Exceptional.PostgreSql/ServiceCollectionExtensions.cs:31`
- Create: `tests/Themia.Exceptional.PostgreSql.IntegrationTests/ExceptionalSchemaProbeTests.cs`

**Interfaces:**
- Consumes: `AddPostgresSchemaProbe` from Task 2.
- Produces: nothing new. `AddThemiaExceptionalPostgres(this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)` keeps its signature.

- [ ] **Step 1: Write the failing test**

`tests/Themia.Exceptional.PostgreSql.IntegrationTests/ExceptionalSchemaProbeTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

public sealed class ExceptionalSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheExceptionsTableIsOffTheSearchPath()
    {
        // "Exceptions" is quoted and case-sensitive: probing it unquoted would fold to lower case
        // and report a false negative, so this test also pins the quoting at the call site.
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS exc_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "exc_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaExceptionalPostgres(
                builder.ConnectionString,
                options => options.ApplicationName = "probe-test"))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
```

If `ExceptionalOptions` has no `ApplicationName` setter, open
`src/neutral/Themia.Exceptional/ExceptionalOptions.cs`, use whichever required property it does
expose, and set that instead — the test needs a valid `configure` delegate, nothing more.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Themia.Exceptional.PostgreSql.IntegrationTests --filter ExceptionalSchemaProbeTests
```

Expected: FAIL — no exception thrown. If it instead fails inside `AddThemiaExceptionalPostgres`
because that method migrates immediately against a search_path with no schema, seed the schema
before building the host as the test already does; the migration writes to `public` regardless.

- [ ] **Step 3: Add the project reference**

In `src/neutral/Themia.Exceptional.PostgreSql/Themia.Exceptional.PostgreSql.csproj`:

```xml
<ProjectReference Include="../Themia.Data.Probes/Themia.Data.Probes.csproj" />
```

- [ ] **Step 4: Register the probe**

In `ServiceCollectionExtensions.cs`, inside `AddThemiaExceptionalPostgres`, before `return services;`:

```csharp
// "Exceptions" is created quoted, so it must be probed quoted -- an unquoted probe folds to
// lower case and would report a table that exists as missing.
services.AddPostgresSchemaProbe(
    "Themia.Exceptional",
    _ =>
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    },
    ["\"Exceptions\""]);
```

Add `using Themia.Data.Probes;` if absent.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/Themia.Exceptional.PostgreSql.IntegrationTests --filter ExceptionalSchemaProbeTests
```

Expected: PASS.

- [ ] **Step 6: Run the package's whole suite**

```bash
dotnet test tests/Themia.Exceptional.PostgreSql.IntegrationTests
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Exceptional.PostgreSql tests/Themia.Exceptional.PostgreSql.IntegrationTests
git commit -m "feat(exceptional): probe Exceptions table resolution at startup"
```

---

### Task 5: Wire `Themia.Challenges.PostgreSql`

**Files:**
- Modify: `src/neutral/Themia.Challenges.PostgreSql/Themia.Challenges.PostgreSql.csproj`
- Modify: `src/neutral/Themia.Challenges.PostgreSql/ServiceCollectionExtensions.cs:19`
- Create: `tests/Themia.Challenges.IntegrationTests/ChallengesSchemaProbeTests.cs`

**Interfaces:**
- Consumes: `AddPostgresSchemaProbe` from Task 2.
- Produces: nothing new. `AddThemiaChallengesPostgres(this IServiceCollection services, string connectionString)` keeps its signature.

- [ ] **Step 1: Write the failing test**

`tests/Themia.Challenges.IntegrationTests/ChallengesSchemaProbeTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Challenges.IntegrationTests;

public sealed class ChallengesSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheChallengeTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS ch_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "ch_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaChallengesPostgres(builder.ConnectionString))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Themia.Challenges.IntegrationTests --filter ChallengesSchemaProbeTests
```

Expected: FAIL — no exception thrown.

- [ ] **Step 3: Add the project reference**

In `src/neutral/Themia.Challenges.PostgreSql/Themia.Challenges.PostgreSql.csproj`:

```xml
<ProjectReference Include="../Themia.Data.Probes/Themia.Data.Probes.csproj" />
```

- [ ] **Step 4: Register the probe**

In `ServiceCollectionExtensions.cs`, inside `AddThemiaChallengesPostgres`, before `return services;`:

```csharp
// Both tables are created unqualified on every engine (see ChallengeSchemaMigration), so both
// follow search_path at runtime while the migration writes them to public.
services.AddPostgresSchemaProbe(
    "Themia.Challenges",
    _ =>
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    },
    ["challenges", "challenge_rate_windows"]);
```

Add `using Themia.Data.Probes;` if absent.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/Themia.Challenges.IntegrationTests --filter ChallengesSchemaProbeTests
```

Expected: PASS.

- [ ] **Step 6: Run the package's whole suite**

```bash
dotnet test tests/Themia.Challenges.IntegrationTests
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Challenges.PostgreSql tests/Themia.Challenges.IntegrationTests
git commit -m "feat(challenges): probe challenge table resolution at startup"
```

---

### Task 6: Wire `Themia.Messaging.PostgreSql`

**Files:**
- Modify: `src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj`
- Modify: `src/neutral/Themia.Messaging.PostgreSql/ServiceCollectionExtensions.cs:27`
- Create: `tests/Themia.Messaging.PostgreSql.IntegrationTests/MessagingSchemaProbeTests.cs` (create the test project if it does not exist, copying `tests/Themia.Data.Probes.IntegrationTests/Themia.Data.Probes.IntegrationTests.csproj` and swapping the `ProjectReference` for `../../src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj`, then `dotnet sln Themia.sln add` it)

**Interfaces:**
- Consumes: `AddPostgresSchemaProbe` from Task 2.
- Produces: nothing new. `AddThemiaMessagingPostgreSql(this IServiceCollection services, string connectionStringName = "Default")` keeps its signature.

This package differs from Tasks 3-5: it takes a connection string **name** and resolves it from
`IConfiguration` at first use, mirroring its private `Resolve(IServiceProvider, string)` helper
(`ServiceCollectionExtensions.cs:52`). The probe factory must resolve it the same way, from the
service provider, not capture a string at registration time.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Messaging.PostgreSql.IntegrationTests;

public sealed class MessagingSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheOutboxTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS msg_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "msg_app";

        using var host = new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = builder.ConnectionString }))
            .ConfigureServices(services => services.AddThemiaMessagingPostgreSql())
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Themia.Messaging.PostgreSql.IntegrationTests --filter MessagingSchemaProbeTests
```

Expected: FAIL — no exception thrown.

- [ ] **Step 3: Add the project reference**

In `src/neutral/Themia.Messaging.PostgreSql/Themia.Messaging.PostgreSql.csproj`, add an
`ItemGroup` with:

```xml
<ProjectReference Include="../Themia.Data.Probes/Themia.Data.Probes.csproj" />
```

- [ ] **Step 4: Register the probe**

In `ServiceCollectionExtensions.cs`, inside `AddThemiaMessagingPostgreSql`, before `return services;`:

```csharp
// Resolved from IServiceProvider, not captured: this package takes a connection string NAME and
// reads the value from IConfiguration, exactly as the dialects above do via Resolve(...).
services.AddPostgresSchemaProbe(
    "Themia.Modules.Messaging",
    sp =>
    {
        var connection = new NpgsqlConnection(Resolve(sp, connectionStringName));
        connection.Open();
        return connection;
    },
    ["messaging_outbox_messages", "messaging_inbox_messages"]);
```

Add `using Themia.Data.Probes;` if absent.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/Themia.Messaging.PostgreSql.IntegrationTests --filter MessagingSchemaProbeTests
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Messaging.PostgreSql tests/Themia.Messaging.PostgreSql.IntegrationTests Themia.sln
git commit -m "feat(messaging): probe outbox and inbox table resolution at startup"
```

---

### Task 7: Wire `Themia.Modules.Pdf` on the PostgreSQL path only

**Files:**
- Modify: `src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj`
- Modify: `src/modules/Themia.Modules.Pdf/DependencyInjection/PdfModuleServiceCollectionExtensions.cs:24` and `:63`
- Create: `tests/Themia.Modules.Pdf.IntegrationTests/PdfSchemaProbeTests.cs` (create the test project if absent, as in Task 6, referencing `../../src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj`)
- Modify: `docs/superpowers/specs/2026-08-23-schema-agreement-design.md`

**Interfaces:**
- Consumes: `AddPostgresSchemaProbe` from Task 2, including its `appliesTo` predicate.
- Produces: nothing new. Both `AddThemiaPdfModuleEfCore(this IServiceCollection services, Action<PdfModuleOptions>? configure = null)` and `AddThemiaPdfModuleDapper(this IServiceCollection services, Action<PdfModuleOptions>? configure = null)` keep their signatures.

Unlike Tasks 3-6 this package serves every engine from one assembly, and it does **not** know the
engine at registration time — it resolves `IDatabaseProvider` from the container and compares
`ProviderName` against `DatabaseProviderNames.Postgres` inside the `AddDbContext` callback
(`PdfModuleServiceCollectionExtensions.cs:32,38`). So the probe is registered unconditionally and
guarded by the `appliesTo` predicate, which runs against the built provider. The spec says
"guarded there" at the registration site; Step 6 corrects that line.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

public sealed class PdfSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenPdfTemplatesIsOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS pdf_app";
            command.ExecuteNonQuery();
        }

        builder.SearchPath = "pdf_app";

        using var host = BuildHost(builder.ConnectionString, DatabaseProviderNames.Postgres);

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_ShouldStart_WhenTheProviderIsNotPostgres()
    {
        // The probe must not run at all off PostgreSQL; an unreachable connection string proves it
        // was never opened.
        using var host = BuildHost(
            "Server=127.0.0.1;Port=1;Database=nothing;Uid=nobody;Pwd=nobody;",
            DatabaseProviderNames.SqlServer);

        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost BuildHost(string connectionString, string providerName)
        => new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString }))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDatabaseProvider>(new StubDatabaseProvider(providerName));
                services.AddThemiaPdfModuleDapper();
            })
            .Build();

    private sealed class StubDatabaseProvider(string providerName) : IDatabaseProvider
    {
        public string ProviderName { get; } = providerName;
    }
}
```

Add the `using` for whichever namespace declares `IDatabaseProvider` and `DatabaseProviderNames`
(`Themia.Framework.Data.EFCore/Abstractions/IDatabaseProvider.cs`). If `IDatabaseProvider` has
members beyond `ProviderName`, implement them on the stub with the simplest valid value.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Themia.Modules.Pdf.IntegrationTests --filter PdfSchemaProbeTests
```

Expected: the first test FAILS (no exception); the second passes trivially.

- [ ] **Step 3: Add the project reference**

In `src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj`:

```xml
<ProjectReference Include="../../neutral/Themia.Data.Probes/Themia.Data.Probes.csproj" />
```

- [ ] **Step 4: Register the probe in the shared helper**

`AddCommon(services, configure)` is called by both entry points
(`PdfModuleServiceCollectionExtensions.cs:28` and `:67`), so registering there covers the EF Core
and the Dapper path with one change. Add at the end of `AddCommon`:

```csharp
// This module serves every engine from one assembly and only learns which one from the
// container, so the probe is registered unconditionally and gated by appliesTo.
services.AddPostgresSchemaProbe(
    "Themia.Modules.Pdf",
    sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var moduleOptions = sp.GetRequiredService<IOptions<PdfModuleOptions>>().Value;
        var connectionString = configuration.GetConnectionString(moduleOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{moduleOptions.ConnectionStringName}' was not found; the PDF module requires it.");
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    },
    ["pdf_templates"],
    appliesTo: sp => sp.GetRequiredService<IDatabaseProvider>().ProviderName
        == DatabaseProviderNames.Postgres);
```

Add `using Npgsql;` and `using Themia.Data.Probes;` if absent. If `Npgsql` is not already a
`PackageReference` of this project, add `<PackageReference Include="Npgsql" />`.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Themia.Modules.Pdf.IntegrationTests --filter PdfSchemaProbeTests
```

Expected: both PASS.

- [ ] **Step 6: Correct the spec**

In `docs/superpowers/specs/2026-08-23-schema-agreement-design.md`, the call-sites table row for
`Themia.Modules.Pdf` says the registration is "guarded by `engine == MigrationEngine.Postgres`".
Replace that cell with:

```
`AddCommon`, reached from both `AddThemiaPdfModuleEfCore` and `AddThemiaPdfModuleDapper`; gated at run time by `appliesTo` on `IDatabaseProvider.ProviderName == DatabaseProviderNames.Postgres`
```

and in the paragraph below the table, replace "it already carries the engine as state
(`PdfModule.cs:14`) and switches on it in DI (`PdfModuleServiceCollectionExtensions.cs:41,44`), so
the registration is guarded there" with "it resolves `IDatabaseProvider` from the container rather
than knowing the engine at registration time (`PdfModuleServiceCollectionExtensions.cs:32,38`), so
the gate is the `appliesTo` predicate, evaluated once at startup".

- [ ] **Step 7: Run the package's whole suite**

```bash
dotnet test tests/Themia.Modules.Pdf.IntegrationTests
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.IntegrationTests docs/superpowers/specs/2026-08-23-schema-agreement-design.md Themia.sln
git commit -m "feat(pdf): probe pdf_templates resolution at startup on PostgreSQL only"
```

---

### Task 8: Full build, full suite, and release note

**Files:**
- Modify: `docs/` release notes file for the next version (find it with `ls docs | grep -i release`; if none exists, create `docs/release-notes-0.17.0.md`)

**Interfaces:**
- Consumes: everything from Tasks 1-7.
- Produces: nothing.

- [ ] **Step 1: Clean build across all target frameworks**

```bash
dotnet build Themia.sln --no-incremental
```

Expected: no errors, no `RS0016`. `TreatWarningsAsErrors` means any warning fails this step.

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test Themia.sln
```

Expected: all pass. Docker must be running for the Testcontainers suites.

- [ ] **Step 3: Write the release note**

Add a section with this content:

```markdown
### New: boot-time PostgreSQL schema probe

`Themia.Data.Probes` is a new neutral package (`net8.0;net10.0`). Five PostgreSQL stores now
verify at host startup that the table they address without a schema actually resolves through the
connection's `search_path`: `Themia.AspNetCore.DataProtection`, `Themia.Exceptional`,
`Themia.Challenges`, `Themia.Modules.Messaging` and `Themia.Modules.Pdf`.

**A table that does not resolve now stops the host.** Previously it surfaced as `42P01` on the
first use of the store — for Data Protection, a user's first request needing an auth cookie, not
the deploy. If your `search_path` does not include the schema holding these tables, the failure
moves from "users cannot sign in some time after a green deploy" to "the container does not
start", and the message names the schema the table is actually in.

A table that resolves outside `public` while a same-named copy also exists in `public` logs a
warning and continues: Themia's migrations write to `public`, so a later migration would alter the
copy your store does not read. The match is by name, so an unrelated `public` table of the same
name produces the warning too.

A probe that cannot reach the database logs a warning and continues. It is not a liveness check
and never fails a boot on a connection error.

No configuration is added and nothing is opt-in. Coord #0088.
```

- [ ] **Step 4: Commit**

```bash
git add docs
git commit -m "docs: release note for the boot-time schema probe"
```

---

## Self-Review

Checked against `docs/superpowers/specs/2026-08-23-schema-agreement-design.md`:

- **New package with the stated dependency set** — Task 1. Driver-free via the connection factory; `Hosting.Abstractions` present.
- **Boot placement following the advisory pattern** — Task 2, with the `AddHostedService<T>` de-duplication trap covered by an explicit test.
- **Assert resolvability, not `public`** — Task 1 test `Host_ShouldStart_WhenTableResolvesOutsidePublic` in Task 2 pins the rev-1 false positive.
- **Mode 1 warning** — Task 2, plus the name-collision caveat carried into the warning text.
- **Connection failure warns and skips** — Task 2, with a dedicated test.
- **All five call sites** — Tasks 3-7, each with the identifiers from the spec's table, `"Exceptions"` quoted.
- **PostgreSQL only** — structural for Tasks 3-6; `appliesTo` for Task 7, with a test that a non-PostgreSQL provider never opens a connection.
- **Spec correction** — Task 7 Step 6: the spec's claim that Pdf is guarded at registration is wrong; it resolves `IDatabaseProvider` at run time.

Gaps deliberately left: the spec's test list includes a `"$user", public` case with a role-named
schema. It is covered in substance by `Host_ShouldStart_WhenTableResolvesOutsidePublic` (a
resolvable table outside `public` must not throw); reproducing the literal `"$user"` expansion
needs a role whose name matches a schema, which Testcontainers' fixed `postgres` role makes
awkward. Note it in the Task 2 commit message rather than skipping it silently.
