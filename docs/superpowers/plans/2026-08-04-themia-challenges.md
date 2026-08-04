# Themia.Challenges Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Challenges` + three engine dialects — a neutral core that issues a one-time secret bound to an opaque key, verifies it exactly once, and enforces TTL, attempt caps and two layers of rate limiting.

**Architecture:** Neutral core (`net8.0;net10.0`) holding the policy engine, the `IChallengeDialect` seam and the FluentMigrator schema; one package per engine supplying the connection and the engine-specific SQL. Follows `Themia.Exceptional` exactly — **not** the Messaging module, which needs a data peer for reasons that do not apply here.

**Tech Stack:** .NET 8 + .NET 10, Dapper, FluentMigrator, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-04-themia-challenges-design.md` (rev 4)

## Global Constraints

- **Never modify or `git add` `CLAUDE.md`.**
- **Never log a secret, a token, or a key.** Secrets are credentials; a key is a phone number or email address, so it is PII. `ChallengeScope` and `ChallengeVerifyResult` both carry the key and are easy to log whole. Log purpose and outcome only.
- **The rate limiter and attempt cap have no off switch.** Values are tunable through options; the mechanism is not removable.
- Core and all three engine packages target `net8.0;net10.0`. Everything must build and test on **both** legs.
- `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` — every public member needs an XML doc comment.
- Every public member goes in `PublicAPI.Unshipped.txt` (RS0016); removals must be deleted from it (RS0017). Const entries use the `const Namespace.Type.Name = value -> type` form.
- **Multi-state outcomes are enums with computed booleans**, never bare bools (`~/.claude/rules/dotnet.md`). A caller must not collapse `RateLimited` into "not verified".
- **One literal table name on every engine** — never `InSchema(...)`, which FluentMigrator drops on MySQL.
- Commit subjects `<type>: <subject>`, imperative, under 72 chars. **Never** add `Co-authored-by:` or "Generated with" trailers.
- Build/test from `Packages/themia/`: `dotnet build Themia.sln`, `dotnet test Themia.sln`.

---

### Task 1: Core contracts and options

**Files:**
- Create: `src/neutral/Themia.Challenges/Themia.Challenges.csproj`
- Create: `src/neutral/Themia.Challenges/ChallengeScope.cs`
- Create: `src/neutral/Themia.Challenges/ChallengeResults.cs`
- Create: `src/neutral/Themia.Challenges/ChallengeFormat.cs`
- Create: `src/neutral/Themia.Challenges/ChallengeOptions.cs`
- Create: `src/neutral/Themia.Challenges/PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Challenges.Tests/Themia.Challenges.Tests.csproj`, `ChallengeOptionsTests.cs`

**Interfaces:**
- Produces: `ChallengeScope(string Key, string Purpose, string? TenantId = null)`; `ChallengeIssueOutcome { Issued, RateLimited }`; `ChallengeVerifyOutcome { Verified, Incorrect, Expired, Consumed, AttemptsExhausted, NotFound }`; `ChallengeIssueResult` (`Outcome`, `Secret`, `ExpiresAt`, `Succeeded => Outcome == Issued`); `ChallengeVerifyResult` (`Outcome`, `Scope`, `Succeeded => Outcome == Verified`); `ChallengeFormat.Numeric(int length)` / `ChallengeFormat.OpaqueToken(int bytes)`; `ChallengeOptions.ConfigurePurpose(string, Action<PurposeOptions>)` where `PurposeOptions` has `Format`, `Ttl`, `MaxAttempts`, `MaxLiveChallenges`, `PerScopeWindow`, `PerKeyWindow`.

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Challenges</PackageId>
    <Description>Themia one-time challenge core — issues a secret bound to an opaque key, verifies it exactly once, and enforces TTL, attempt caps and two layers of rate limiting. Serves phone OTP, email OTP, magic links, email verification and password reset. No SMS, email, user or framework dependency.</Description>
    <PackageTags>themia;otp;challenge;authentication;security</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
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
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Challenges.Tests" />
    <InternalsVisibleTo Include="Themia.Challenges.IntegrationTests" />
  </ItemGroup>
</Project>
```

Add both projects to `Themia.sln` (`dotnet sln Themia.sln add ...`).

- [ ] **Step 2: Write the failing options test**

`tests/Themia.Challenges.Tests/ChallengeOptionsTests.cs`:

```csharp
using Themia.Challenges;
using Xunit;

namespace Themia.Challenges.Tests;

public class ChallengeOptionsTests
{
    [Fact]
    public void ConfigurePurpose_ShouldRoundTripTheSettings()
    {
        var options = new ChallengeOptions();
        options.ConfigurePurpose("login", p =>
        {
            p.Format = ChallengeFormat.Numeric(6);
            p.Ttl = TimeSpan.FromMinutes(5);
            p.MaxAttempts = 5;
        });

        var purpose = options.GetPurpose("login");

        Assert.Equal(6, purpose.Format.Length);
        Assert.Equal(TimeSpan.FromMinutes(5), purpose.Ttl);
        Assert.Equal(5, purpose.MaxAttempts);
    }

    [Fact]
    public void GetPurpose_ShouldThrow_WhenPurposeWasNeverConfigured()
    {
        var options = new ChallengeOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => options.GetPurpose("login"));

        Assert.Contains("login", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigurePurpose", ex.Message, StringComparison.Ordinal);
    }

    // The mechanism is not removable — only its values are tunable.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxAttempts_ShouldThrow_WhenNotPositive(int value)
    {
        var options = new ChallengeOptions();

        Assert.ThrowsAny<ArgumentException>(() =>
            options.ConfigurePurpose("login", p => p.MaxAttempts = value));
    }

    [Fact]
    public void PerKeyWindow_ShouldThrow_WhenLimitIsNotPositive()
    {
        var options = new ChallengeOptions();

        Assert.ThrowsAny<ArgumentException>(() =>
            options.ConfigurePurpose("login", p => p.PerKeyWindow = (Limit: 0, Window: TimeSpan.FromMinutes(15))));
    }
}
```

- [ ] **Step 3: Run it and confirm it fails**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~ChallengeOptionsTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 4: Implement the contracts**

`ChallengeScope.cs`:

```csharp
namespace Themia.Challenges;

/// <summary>
/// Identity of a challenge. Tenant is part of it deliberately: two tenants may hold the same phone
/// number, so without it a code issued to one tenant would verify under another.
/// </summary>
/// <param name="Key">The opaque key — a phone number, an email address, a user id. Never parsed.</param>
/// <param name="Purpose">Scopes the challenge and selects its configuration.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a platform-level challenge.</param>
public sealed record ChallengeScope(string Key, string Purpose, string? TenantId = null);
```

`ChallengeResults.cs` — the two enums and two results, each with a computed `Succeeded`. `ChallengeIssueResult.Secret` is the plaintext and is only non-null when `Outcome == Issued`; its XML doc must state that this is the single moment the plaintext exists.

`ChallengeFormat.cs` — a sealed class with `static ChallengeFormat Numeric(int length)` and `static ChallengeFormat OpaqueToken(int bytes)`, exposing `Kind` and `Length`.

`ChallengeOptions.cs` — `ConfigurePurpose` validates eagerly (throws inside the callback's assignment via property setters) and `GetPurpose` throws naming both the purpose and `ConfigurePurpose` when unknown.

- [ ] **Step 5: Run to green, declare the API, commit**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~ChallengeOptionsTests"` → PASS
Run: `dotnet build Themia.sln --no-incremental` → 0 warnings (fix RS0016 by filling `PublicAPI.Unshipped.txt`)

```bash
git add src/neutral/Themia.Challenges tests/Themia.Challenges.Tests Themia.sln
git commit -m "feat(challenges): core contracts and per-purpose options"
```

---

### Task 2: Secret generation and hashing

**Files:**
- Create: `src/neutral/Themia.Challenges/Internal/SecretGenerator.cs`
- Create: `src/neutral/Themia.Challenges/Internal/SecretHasher.cs`
- Test: `tests/Themia.Challenges.Tests/SecretGeneratorTests.cs`, `SecretHasherTests.cs`

**Interfaces:**
- Consumes: `ChallengeFormat` from Task 1.
- Produces: `internal static class SecretGenerator { static string Generate(ChallengeFormat format); }` and `internal static class SecretHasher { static (string Hash, string Salt) Hash(string secret); static bool Verify(string secret, string hash, string salt); }`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class SecretGeneratorTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Numeric_ShouldProduceExactlyThatManyDigits(int length)
    {
        var secret = SecretGenerator.Generate(ChallengeFormat.Numeric(length));

        Assert.Equal(length, secret.Length);
        Assert.All(secret, c => Assert.InRange(c, '0', '9'));
    }

    // Leading zeros must survive: a code rendered from an int would turn "004821" into "4821"
    // and the user's six-digit entry would never match.
    [Fact]
    public void Numeric_ShouldPreserveLeadingZeros()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 2000; i++) seen.Add(SecretGenerator.Generate(ChallengeFormat.Numeric(6)));

        Assert.All(seen, s => Assert.Equal(6, s.Length));
    }

    [Fact]
    public void Numeric_ShouldNotRepeatAcrossManyDraws()
    {
        var draws = Enumerable.Range(0, 500).Select(_ => SecretGenerator.Generate(ChallengeFormat.Numeric(6))).ToList();

        // A constant or a low-entropy source shows up immediately as a tiny distinct count.
        Assert.True(draws.Distinct().Count() > 400, $"only {draws.Distinct().Count()} distinct of 500");
    }
}

public class SecretHasherTests
{
    [Fact]
    public void Verify_ShouldAcceptTheOriginalSecret()
    {
        var (hash, salt) = SecretHasher.Hash("483920");

        Assert.True(SecretHasher.Verify("483920", hash, salt));
    }

    [Fact]
    public void Verify_ShouldRejectADifferentSecret()
    {
        var (hash, salt) = SecretHasher.Hash("483920");

        Assert.False(SecretHasher.Verify("483921", hash, salt));
    }

    // The stored hash must not be the secret, and two identical secrets must not produce identical rows.
    [Fact]
    public void Hash_ShouldNotContainThePlaintext_AndShouldSaltPerCall()
    {
        var (hashA, saltA) = SecretHasher.Hash("483920");
        var (hashB, saltB) = SecretHasher.Hash("483920");

        Assert.DoesNotContain("483920", hashA, StringComparison.Ordinal);
        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(hashA, hashB);
    }
}
```

- [ ] **Step 2: Run and confirm failure** — `dotnet test Themia.sln --filter "SecretGeneratorTests|SecretHasherTests"` → FAIL.

- [ ] **Step 3: Implement**

`SecretGenerator` uses `RandomNumberGenerator.GetInt32` per digit (never `Random`), and formats with `ToString()` on a char buffer so leading zeros survive. `OpaqueToken` uses `RandomNumberGenerator.GetBytes` + `Base64Url`.

`SecretHasher` uses a per-secret random salt and `CryptographicOperations.FixedTimeEquals` on the hashes. Document precisely what hashing buys, per the spec: it prevents casual disclosure, not a determined attacker with the backup — a 6-digit space falls to a GPU regardless. Short TTL and single-use are what make a leaked row worthless.

- [ ] **Step 4: Green, then commit**

```bash
git add src/neutral/Themia.Challenges tests/Themia.Challenges.Tests
git commit -m "feat(challenges): secret generation and salted hashing"
```

---

### Task 3: Storage seam and the FluentMigrator schema

**Files:**
- Create: `src/neutral/Themia.Challenges/IChallengeDialect.cs`
- Create: `src/neutral/Themia.Challenges/Migrations/ChallengeSchemaMigration.cs`
- Test: `tests/Themia.Challenges.Tests/ChallengeDialectContractTests.cs`

**Interfaces:**
- Produces: `IChallengeDialect` with `DbConnection CreateConnection()` plus the SQL statements the engine runs — `InsertSql`, `SelectLiveByScopeSql`, `SelectLiveByTokenHashSql`, `ConsumeSql`, `RecordAttemptSql`, `InvalidateLiveForScopeSql`, `PurgeExpiredSql`, and the counter statements `IncrementWindowSql`, `SelectWindowCountsSql`, `DecrementWindowSql`, `PurgeElapsedWindowsSql`. Tasks 4-6 implement it per engine; Task 7's engine reads it.

- [ ] **Step 1: Write the contract test**

The dialect is an interface, so the test that earns its place is the one asserting the SQL contract every implementation must satisfy — that `ConsumeSql` is a single conditional statement, not a read followed by a write.

```csharp
// A dialect whose ConsumeSql does not carry its own guard makes atomicity the caller's problem,
// which is exactly how a read-then-write regression gets in later.
[Theory]
[MemberData(nameof(AllDialects))]
public void ConsumeSql_ShouldBeConditional(IChallengeDialect dialect)
{
    var sql = dialect.ConsumeSql;

    Assert.Contains("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("consumed_at IS NULL", sql, StringComparison.OrdinalIgnoreCase);
}
```

`AllDialects` returns the three implementations once Tasks 4-6 land; until then it returns the one under construction. State that dependency in the file's comment so a later reader does not think the theory is dead.

- [ ] **Step 2: Write the migration**

`Migrations/ChallengeSchemaMigration.cs`, modelled on `src/neutral/Themia.Exceptional/Migrations/ExceptionLogMigration.cs` and `Themia.Modules.Messaging`'s schema migration:

```csharp
[Migration(202608040001, "Themia.Challenges: create challenges and challenge_rate_windows")]
public sealed class ChallengeSchemaMigration : Migration
```

Two tables, **unprefixed literal names on every engine** (`challenges`, `challenge_rate_windows`), never `InSchema(...)`:

`challenges` — `id` (guid PK), `tenant_id` (nullable, 100), `key` (450), `purpose` (100), `secret_hash`, `secret_salt`, `token_hash` (nullable), `attempts` (int), `expires_at`, `consumed_at` (nullable), `created_at`.

`challenge_rate_windows` — `id` (guid PK), `tenant_id` (nullable), `key` (450), `purpose` (nullable — **null means the per-key ceiling across all purposes**), `window_start`, `count` (int).

Indexes: plain on `(tenant_id, key, purpose)`; on `token_hash`; on `(tenant_id, key, purpose, window_start)`.

**Not unique** on `(tenant_id, key, purpose)`, and this corrects an earlier draft of this plan that said unique. `PurposeOptions.MaxLiveChallenges` is configurable above 1 precisely so a late-arriving first code can still verify (spec, "Re-issue policy") — a unique index makes more than one live challenge per scope impossible and breaks that option the moment anyone sets it. Uniqueness of the *live* challenge is a policy the engine enforces when it invalidates, not a database constraint.

Note in the migration's XML doc **why `key` is 450 and not longer**: SQL Server caps an indexed nvarchar key at 450 characters.

- [ ] **Step 3: Build, declare API, commit**

```bash
git add src/neutral/Themia.Challenges tests/Themia.Challenges.Tests
git commit -m "feat(challenges): dialect seam and FluentMigrator schema"
```

---

### Task 4: PostgreSQL dialect

**Files:**
- Create: `src/neutral/Themia.Challenges.PostgreSql/Themia.Challenges.PostgreSql.csproj`
- Create: `src/neutral/Themia.Challenges.PostgreSql/PostgresChallengeDialect.cs`
- Create: `src/neutral/Themia.Challenges.PostgreSql/ServiceCollectionExtensions.cs`
- Create: `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `IChallengeDialect` (Task 3).
- Produces: `AddThemiaChallengesPostgres(this IServiceCollection services, string connectionString)`. Tasks 5-6 mirror this shape for MySQL and SQL Server; Task 7's registration guard looks for `IChallengeDialect`.

Model the csproj on `src/neutral/Themia.Exceptional.PostgreSql/Themia.Exceptional.PostgreSql.csproj` (`net8.0;net10.0`, `Npgsql`, project references to the core and `Themia.Data.Migrations`), and the DI shape on its `ServiceCollectionExtensions.AddThemiaExceptionalPostgres`.

- [ ] **Step 1: Write `ConsumeSql` first, as a test**

The atomic consume is the reason this package exists in a database rather than a cache. On PostgreSQL:

```sql
UPDATE challenges
   SET consumed_at = @Now
 WHERE id = @Id AND consumed_at IS NULL AND expires_at > @Now
```

Rows affected of 1 means this caller won; 0 means someone else did, or it expired. Write the contract test from Task 3 against `PostgresChallengeDialect` and confirm it fails before the class exists.

- [ ] **Step 2: Implement the dialect** — `CreateConnection()` returns `new NpgsqlConnection(connectionString)`; every statement uses named parameters; `IncrementWindowSql` uses `INSERT ... ON CONFLICT ... DO UPDATE SET count = challenge_rate_windows.count + 1` so a concurrent issue cannot lose a count.

- [ ] **Step 3: Implement the DI extension**, mirroring `AddThemiaExceptionalPostgres`: validate the connection string, register the dialect as `IChallengeDialect`, and register the migration engine.

- [ ] **Step 4: Green, declare API, commit**

```bash
git add src/neutral/Themia.Challenges.PostgreSql Themia.sln
git commit -m "feat(challenges): PostgreSQL dialect"
```

---

### Task 5: MySQL dialect

**Files:** mirror Task 4 under `src/neutral/Themia.Challenges.MySql/`, package `MySqlConnector`.

**Interfaces:** produces `AddThemiaChallengesMySql(...)`.

- [ ] **Step 1: Write the dialect with MySQL's upsert form**

`IncrementWindowSql` uses `INSERT ... ON DUPLICATE KEY UPDATE count = count + 1`. `ConsumeSql` is the same conditional `UPDATE` as PostgreSQL — MySQL reports affected rows the same way.

- [ ] **Step 2: Note the engine-specific trap in a comment** — MySQL treats schema and database as the same concept, which is why the schema migration uses unqualified table names on every engine (the defect that made Messaging's `outbox_messages` collide with Notifications').

- [ ] **Step 3: Green, declare API, commit**

```bash
git add src/neutral/Themia.Challenges.MySql Themia.sln
git commit -m "feat(challenges): MySQL dialect"
```

---

### Task 6: SQL Server dialect

**Files:** mirror Task 4 under `src/neutral/Themia.Challenges.SqlServer/`, package `Microsoft.Data.SqlClient`.

**Interfaces:** produces `AddThemiaChallengesSqlServer(...)`.

- [ ] **Step 1: Write the dialect**

`IncrementWindowSql` uses `MERGE` **with `HOLDLOCK`** — a `MERGE` without it races under concurrent inserts and can throw a duplicate-key error instead of incrementing. Put that reason in a comment; it is the single most common SQL Server upsert bug.

`ConsumeSql` is the same conditional `UPDATE`; `@@ROWCOUNT` gives the same 1-or-0 answer.

- [ ] **Step 2: Green, declare API, commit**

```bash
git add src/neutral/Themia.Challenges.SqlServer Themia.sln
git commit -m "feat(challenges): SQL Server dialect"
```

---

### Task 7: The policy engine

**Files:**
- Create: `src/neutral/Themia.Challenges/IChallengeService.cs`
- Create: `src/neutral/Themia.Challenges/Internal/ChallengeService.cs`
- Create: `src/neutral/Themia.Challenges/DependencyInjection/ChallengeServiceCollectionExtensions.cs`
- Test: `tests/Themia.Challenges.Tests/ChallengeServiceTests.cs`, `RegistrationTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3; a dialect from Tasks 4-6.
- Produces: `IChallengeService` with `IssueAsync`, `VerifyAsync`, `VerifyByTokenAsync`, `RefundAsync`, exactly as the spec's Public API section defines; `AddThemiaChallenges(this IServiceCollection, Action<ChallengeOptions>)`.

- [ ] **Step 1: Write the failing behaviour tests**

These are the security requirements. Each must fail if its requirement is removed.

```csharp
// Two layers. The per-purpose layer protects UX; only the per-key layer protects the SMS invoice.
[Fact] public async Task Issue_ShouldRateLimit_PerScope()
[Fact] public async Task Issue_ShouldRateLimit_PerKeyAcrossPurposes()   // cycle purposes, still refused
[Fact] public async Task Refund_ShouldReturnQuota_SoAFailedDeliveryDoesNotConsumeIt()

[Fact] public async Task Verify_ShouldReturnVerified_ForTheCorrectSecret()
[Fact] public async Task Verify_ShouldReturnIncorrect_ForAWrongSecret()
[Fact] public async Task Verify_ShouldReturnExpired_AfterTheTtl()
[Fact] public async Task Verify_ShouldReturnConsumed_OnTheSecondUse()
[Fact] public async Task Verify_ShouldReturnAttemptsExhausted_AfterMaxAttempts()

// Re-issue invalidates by default (MaxLiveChallenges = 1) — and the test names the UX consequence.
[Fact] public async Task Issue_ShouldInvalidateTheOutstandingChallenge_WhenMaxLiveIsOne()
[Fact] public async Task Issue_ShouldKeepBothLive_WhenMaxLiveIsTwo()

// Tenant is part of the identity, not decoration.
[Fact] public async Task Verify_ShouldNotMatchAcrossTenants()
[Fact] public async Task RateLimit_ShouldNotLeakAcrossTenants()

// v1 has no token generator; the failure must not look like an expired token.
[Fact] public async Task VerifyByToken_ShouldThrowNotSupported_InV1()
```

The engine talks to a dialect, so these run against a **fake dialect backed by a dictionary** implementing `IChallengeDialect`'s statements as in-memory operations. That fake is a test double only — it never ships, and the atomicity it cannot prove is covered by Task 8.

- [ ] **Step 2: Confirm they fail** — `dotnet test Themia.sln --filter "FullyQualifiedName~ChallengeServiceTests"`.

- [ ] **Step 3: Implement `ChallengeService`**

`IssueAsync`: resolve purpose config → check per-key window, then per-scope window → if either refuses, return `RateLimited` **without generating a secret** → invalidate outstanding challenges beyond `MaxLiveChallenges` → generate, hash, insert → increment both windows → return the plaintext once.

`VerifyAsync`: find live rows for the scope → constant-time compare each → on match run `ConsumeSql` and treat 0 rows affected as `Consumed` (someone else won the race) → on mismatch run `RecordAttemptSql` and return `AttemptsExhausted` when the cap is reached, otherwise `Incorrect`.

`VerifyByTokenAsync`: `throw new NotSupportedException` naming the unshipped opaque-token format. **Not `NotFound`** — that reads as an expired token and sends an adopter debugging their own storage.

`RefundAsync`: decrement both windows, floored at zero.

- [ ] **Step 4: Implement registration with the mandatory-dialect guard**

```csharp
public static IServiceCollection AddThemiaChallenges(this IServiceCollection services, Action<ChallengeOptions> configure)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);

    var options = new ChallengeOptions();
    configure(options);
    options.Validate();

    services.TryAddSingleton(options);
    services.TryAddSingleton(TimeProvider.System);
    services.TryAddScoped<IChallengeService, ChallengeService>();
    return services;
}
```

The dialect guard cannot run here — engine packages register *after* the core, so scanning the collection at this point would always fail. Enforce it at **first resolution** instead: `ChallengeService`'s constructor takes `IChallengeDialect` and DI throws if none is registered. Wrap that in a clearer message by resolving `IChallengeDialect?` and throwing an `InvalidOperationException` naming `AddThemiaChallenges{Postgres|MySql|SqlServer}(...)`.

Write `RegistrationTests` proving: core alone → resolving `IChallengeService` throws naming the engine methods; core + fake dialect → resolves.

- [ ] **Step 5: Green, declare API, commit**

```bash
git add src/neutral/Themia.Challenges tests/Themia.Challenges.Tests
git commit -m "feat(challenges): policy engine, rate limiting and registration"
```

---

### Task 8: Integration tests on all three engines

**Files:**
- Create: `tests/Themia.Challenges.IntegrationTests/Themia.Challenges.IntegrationTests.csproj`
- Create: `ChallengeStoreTests.cs`, `ConcurrencyTests.cs`, `RetentionTests.cs`

Model the fixtures on `tests/Themia.Modules.Messaging.IntegrationTests/` (Testcontainers, one container per engine, `net10.0` test project even though the packages multi-target — the containers do not care).

- [ ] **Step 1: Schema applies on all three engines**

Run `ChallengeSchemaMigration` against each container and assert both tables and every index exist. This is what catches an `InSchema(...)` that silently vanished on MySQL.

- [ ] **Step 2: The concurrency test — the one that cannot be faked**

```csharp
// Exactly one of two simultaneous verifications may win. This is the requirement most likely to be
// quietly broken by a later refactor to read-then-write, and it cannot be proven in memory.
[Fact]
public async Task TwoSimultaneousVerifications_ExactlyOneWins()
{
    var issue = await service.IssueAsync(scope);

    var results = await Task.WhenAll(
        service.VerifyAsync(scope, issue.Secret!),
        service.VerifyAsync(scope, issue.Secret!));

    Assert.Equal(1, results.Count(r => r.Outcome == ChallengeVerifyOutcome.Verified));
    Assert.Equal(1, results.Count(r => r.Outcome == ChallengeVerifyOutcome.Consumed));
}
```

- [ ] **Step 3: Retention must not reset a rate limit**

```csharp
// The specific failure that made an earlier design wrong: counters and challenges in one table meant
// purging the challenges reset the ceiling that protects the SMS bill.
[Fact]
public async Task PurgingChallenges_ShouldNotResetThePerKeyCeiling()
{
    // exhaust the per-key ceiling, purge challenge rows, assert the next issue is still RateLimited
}
```

- [ ] **Step 4: Hashing is real** — read the persisted row directly and assert the plaintext secret does not appear in any column.

- [ ] **Step 5: Run all three engines**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Challenges.IntegrationTests"`
Docker required. If unavailable, report it — do not skip or delete the tests.

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Challenges.IntegrationTests Themia.sln
git commit -m "test(challenges): integration tests across three engines"
```

---

### Task 9: Retention purge and release prep

**Files:**
- Create: `src/neutral/Themia.Challenges/Internal/ChallengePurgeService.cs`
- Modify: `CHANGELOG.md`, `Directory.Build.props`
- Test: `tests/Themia.Challenges.Tests/ChallengePurgeServiceTests.cs`

- [ ] **Step 1: Implement the purge as an `IHostedService`**

Two retentions, per the spec: challenge rows after `ChallengeRetentionHours` (default 24), rate windows only once fully elapsed. Off when `PurgeEnabled` is false. Mirror `Themia.Modules.Messaging`'s drainer-integrated purge, including its lesson: **advance the next-purge gate only after the purge succeeds**, so a transient failure retries rather than suppressing retention for a full cycle.

- [ ] **Step 2: Test that a failed purge retries** rather than advancing the gate.

- [ ] **Step 3: Whole-solution verification**

Run: `dotnet build Themia.sln --no-incremental` → 0 warnings, 0 errors
Run: `dotnet test Themia.sln` → all green on both TFM legs

- [ ] **Step 4: CHANGELOG under `[Unreleased]`**

Add the four packages under **Added**, naming what they are for and the two behaviours an adopter must know: re-issue invalidates by default, and the per-key ceiling is a lockout vector that `RefundAsync` mitigates.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Challenges tests/Themia.Challenges.Tests CHANGELOG.md
git commit -m "feat(challenges): retention purge and changelog"
```

---

## Self-Review

**Spec coverage:** Public API → Tasks 1, 7. Two shapes → Task 2 (generator) and Task 7 (`VerifyByTokenAsync` throwing in v1). Storage and retention two-table split → Tasks 3, 9, and pinned by Task 8 Step 3. Registration guard → Task 7 Step 4. All eight security requirements → Task 7 Step 1 plus Task 8. Both consumers adopting without a peer → Tasks 4-6 (dialects own their connection). Identity integration and the opaque-token generator are explicitly out of scope for v1 and appear in no task.

**Placeholder scan:** no TBD. The one deliberately deferred item is Task 3's `AllDialects` member data, which cannot list all three implementations until Tasks 4-6 exist — stated in the step rather than left implicit.

**Type consistency:** `ChallengeScope`, `ChallengeIssueResult`, `ChallengeVerifyResult`, `ChallengeFormat`, `IChallengeDialect`, `IChallengeService` are spelled identically across Tasks 1-9. `AddThemiaChallenges{Postgres|MySql|SqlServer}` matches the error message text in Task 7 Step 4.

**Known risk:** Task 7 Step 4 is the awkward one — the mandatory-dialect guard cannot be a registration-time collection scan because engine packages register after the core, unlike every other guard in this codebase. Deferring it to first resolution is the honest option, and the `RegistrationTests` in that step are what stop it degrading into a raw DI activation error.
