# Identity External-Auth + Token Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract Themia's JWT access-token issuance and external-auth protocol/flow out of `Themia.Modules.Identity.AspNetCore` into two new persistence-free packages so a bring-your-own-user-store consumer (ezy-assets) can adopt them without the `identity.*` schema.

**Architecture:** Two new net10 packages — `Themia.Modules.Identity.Tokens.AspNetCore` (JWT issuance: `AccessTokenService`, signing, `JwtOptions`, `AuthTokenIssuer`) and `Themia.Modules.Identity.ExternalAuth.AspNetCore` (provider/registry/builder/flow/hooks/endpoints) — each depending only on `Themia.Modules.Identity.Abstractions` (+ neutral `Themia.AspNetCore`, + Tokens for ExternalAuth). `Themia.Modules.Identity.AspNetCore` keeps the local/password flow + JwtBearer scheme and re-references both new packages so bundled behavior is unchanged. The external-login flow registration is re-homed into `AddThemiaExternalAuth` behind an external-only DI guard (requires `IExternalLoginService` + token seams, **not** `IUserService`).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `Microsoft.IdentityModel.*` (8.19.1), xUnit, PublicAPI analyzers, central package management.

**Spec:** `docs/superpowers/specs/2026-06-23-identity-externalauth-extraction-design.md`. Coord #0011. Version 0.6.6.

---

## Conventions (every task)
- TDD where there's behavior; for verbatim file relocations, the "test" is the build + the moved tests passing.
- `System.Text.Json` only; `ILogger<T>` only; THEMIA101 (no log-and-rethrow). `TreatWarningsAsErrors` is on — XML docs required on public members (RS0016); removed shipped public API trips RS0017.
- CPM: PackageReferences carry NO `Version`. Mirror the existing csproj idioms exactly.
- Commit messages imperative, no co-author trailers. Branch is `feature/identity-externalauth-extraction` (already created).
- After any task that changes public API, run `dotnet build Themia.sln --no-incremental` and fix RS0016/RS0017 by editing the relevant `PublicAPI.*.txt`.
- **Namespaces change on move:** files moved to the Tokens package take `Themia.Modules.Identity.Tokens.AspNetCore[.Sub]`; files moved to the ExternalAuth package take `Themia.Modules.Identity.ExternalAuth.AspNetCore[.Sub]`. Update every `namespace` and every cross-`using` accordingly.

---

## File structure (target)

```
src/modules/Themia.Modules.Identity.Tokens.AspNetCore/        (NEW, net10, persistence-free)
  Themia.Modules.Identity.Tokens.AspNetCore.csproj
  AssemblyInfo.cs                         # InternalsVisibleTo ExternalAuth + Identity.AspNetCore (+ test projects)
  Tokens/ AccessTokenService.cs, JwtClaimNames.cs
  Signing/ IJwtSigningCredentialsProvider.cs, SymmetricSigningCredentialsProvider.cs
  Options/ JwtOptions.cs
  Authentication/ AuthTokenIssuer.cs      # internal static (shared issuer)
  DependencyInjection/ IdentityTokensServiceCollectionExtensions.cs   # AddThemiaIdentityTokens
  PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt

src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore/  (NEW, net10)
  Themia.Modules.Identity.ExternalAuth.AspNetCore.csproj
  External/ OidcExternalAuthProvider.cs, OidcProviderConfig.cs, ExternalAuthProviderRegistry.cs,
            ExternalAuthenticationFlow.cs, ExternalAuthenticationHooksBase.cs
  DependencyInjection/ ExternalAuthBuilder.cs   # AddThemiaExternalAuth + builder + flow/hooks registration + guard
  Endpoints/ IdentityExternalAuthEndpoints.cs
  Options/ ExternalAuthOptions.cs               # + GoogleOptions/LineOptions if separate
  PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt

src/modules/Themia.Modules.Identity.AspNetCore/              (CHANGED)
  Authentication/ AuthenticationFlow.cs, AuthenticationHooksBase.cs   (STAY)
  Endpoints/ IdentityAuthEndpoints.cs                                  (STAY)
  DependencyInjection/ IdentityAspNetCoreServiceCollectionExtensions.cs (re-wired)
  + the JwtBearer scheme registration (STAYS; uses JwtOptions/signing from Tokens pkg)
  csproj: + ProjectReference Tokens.AspNetCore + ExternalAuth.AspNetCore

tests/
  Themia.Modules.Identity.Tokens.AspNetCore.Tests/        (NEW — moved token/signing tests)
  Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests/  (NEW — moved OIDC tests + BYO test)
  Themia.Modules.Identity.AspNetCore.Tests/               (keeps local/password + JwtBearer tests)
```

> **Before starting:** run `grep -rn "namespace " src/modules/Themia.Modules.Identity.AspNetCore/{Tokens,Signing,Options/JwtOptions.cs,Authentication/AuthTokenIssuer.cs,External,Endpoints/IdentityExternalAuthEndpoints.cs,DependencyInjection/ExternalAuthBuilder.cs}` to capture every source namespace and `grep -rln "Themia.Modules.Identity.AspNetCore.\(Tokens\|Signing\|Options\|External\|Authentication.AuthTokenIssuer\)"` to find every consumer that needs a `using` update. The moves below assume you update these as you go.

---

# Phase A — `Themia.Modules.Identity.Tokens.AspNetCore`

### Task 1: Scaffold the Tokens package

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Tokens.AspNetCore/Themia.Modules.Identity.Tokens.AspNetCore.csproj`
- Create: `src/modules/Themia.Modules.Identity.Tokens.AspNetCore/PublicAPI.Shipped.txt` (just `#nullable enable`), `PublicAPI.Unshipped.txt` (empty + `#nullable enable`)
- Create: `src/modules/Themia.Modules.Identity.Tokens.AspNetCore/AssemblyInfo.cs`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the csproj** (mirrors Abstractions/AspNetCore idioms; persistence-free — references only Abstractions + IdentityModel):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Identity.Tokens.AspNetCore</PackageId>
    <Description>Persistence-free JWT access-token issuance for Themia Identity — AccessTokenService, symmetric signing, JwtOptions, and the shared access+refresh token issuer. No EF/Dapper, no user store.</Description>
    <PackageTags>themia;identity;jwt;tokens;authentication;aspnetcore</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />
    <PackageReference Include="Microsoft.IdentityModel.Tokens" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
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

> If `AccessTokenService`/signing need `Microsoft.Extensions.Logging.Abstractions` or others, add those PackageReferences (they're in `Directory.Packages.props`); discover from the build in Task 2. Do NOT add `Microsoft.AspNetCore.App` unless a moved file actually uses ASP.NET Core types (it shouldn't — verify).

- [ ] **Step 2: Create `AssemblyInfo.cs`** — the shared `AuthTokenIssuer` is internal; grant the two consuming assemblies + the test project access:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Themia.Modules.Identity.ExternalAuth.AspNetCore")]
[assembly: InternalsVisibleTo("Themia.Modules.Identity.AspNetCore")]
[assembly: InternalsVisibleTo("Themia.Modules.Identity.Tokens.AspNetCore.Tests")]
```

- [ ] **Step 3: Seed PublicAPI files** — `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` each contain a single line `#nullable enable`.

- [ ] **Step 4: Add to the solution**

Run: `cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia && dotnet sln Themia.sln add src/modules/Themia.Modules.Identity.Tokens.AspNetCore/Themia.Modules.Identity.Tokens.AspNetCore.csproj`

- [ ] **Step 5: Build the empty project** — Run: `dotnet build src/modules/Themia.Modules.Identity.Tokens.AspNetCore/Themia.Modules.Identity.Tokens.AspNetCore.csproj` — Expected: succeeds (no source yet).

- [ ] **Step 6: Commit**

```bash
git add src/modules/Themia.Modules.Identity.Tokens.AspNetCore Themia.sln
git commit -m "feat: scaffold Themia.Modules.Identity.Tokens.AspNetCore package"
```

---

### Task 2: Move the JWT-issuance stack into the Tokens package

**Files (move + renamespace; content otherwise unchanged):**
- Move: `Tokens/AccessTokenService.cs`, `Tokens/JwtClaimNames.cs`, `Signing/IJwtSigningCredentialsProvider.cs`, `Signing/SymmetricSigningCredentialsProvider.cs`, `Options/JwtOptions.cs`, `Authentication/AuthTokenIssuer.cs` from `Themia.Modules.Identity.AspNetCore/` → `Themia.Modules.Identity.Tokens.AspNetCore/` (same sub-folders).

- [ ] **Step 1: git mv the files**

```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
S=src/modules/Themia.Modules.Identity.AspNetCore
D=src/modules/Themia.Modules.Identity.Tokens.AspNetCore
mkdir -p $D/Tokens $D/Signing $D/Options $D/Authentication
git mv $S/Tokens/AccessTokenService.cs $D/Tokens/
git mv $S/Tokens/JwtClaimNames.cs $D/Tokens/
git mv $S/Signing/IJwtSigningCredentialsProvider.cs $D/Signing/
git mv $S/Signing/SymmetricSigningCredentialsProvider.cs $D/Signing/
git mv $S/Options/JwtOptions.cs $D/Options/
git mv $S/Authentication/AuthTokenIssuer.cs $D/Authentication/
```

- [ ] **Step 2: Renamespace the moved files** — in each moved file change the namespace prefix `Themia.Modules.Identity.AspNetCore` → `Themia.Modules.Identity.Tokens.AspNetCore` (e.g. `…AspNetCore.Tokens` → `…Tokens.AspNetCore.Tokens`, `…AspNetCore.Signing` → `…Tokens.AspNetCore.Signing`, `…AspNetCore.Options` (the JwtOptions one only) → `…Tokens.AspNetCore.Options`, `…AspNetCore.Authentication` (AuthTokenIssuer only) → `…Tokens.AspNetCore.Authentication`). Fix any intra-stack `using` between these files to the new namespaces. Keep `AuthTokenIssuer` `internal static` and `JwtOptions`/`JwtClaimNames`/`AccessTokenService`/signing types as-is (public).

```bash
for f in $D/Tokens/AccessTokenService.cs $D/Tokens/JwtClaimNames.cs $D/Signing/IJwtSigningCredentialsProvider.cs $D/Signing/SymmetricSigningCredentialsProvider.cs $D/Options/JwtOptions.cs $D/Authentication/AuthTokenIssuer.cs; do
  sed -i '' 's/namespace Themia\.Modules\.Identity\.AspNetCore\./namespace Themia.Modules.Identity.Tokens.AspNetCore./' "$f"
  sed -i '' 's/using Themia\.Modules\.Identity\.AspNetCore\.Options;/using Themia.Modules.Identity.Tokens.AspNetCore.Options;/' "$f"
  sed -i '' 's/using Themia\.Modules\.Identity\.AspNetCore\.Signing;/using Themia.Modules.Identity.Tokens.AspNetCore.Signing;/' "$f"
done
```

- [ ] **Step 3: Build the Tokens project to surface RS0016 + missing PackageReferences** — Run: `dotnet build $D/Themia.Modules.Identity.Tokens.AspNetCore.csproj --no-incremental`. Add any missing PackageReferences flagged (e.g. logging/options) to the csproj. Then add every public-member line the analyzer reports under RS0016 to `$D/PublicAPI.Unshipped.txt`.

- [ ] **Step 4: Re-point `Identity.AspNetCore` at the moved types** — add the ProjectReference and fix usings:

```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet add $S/Themia.Modules.Identity.AspNetCore.csproj reference $D/Themia.Modules.Identity.Tokens.AspNetCore.csproj
# update usings in the files that STAY but referenced the moved types (AuthenticationFlow, AccessTokenService consumers, JwtBearer scheme, the DI extension):
grep -rl "Themia.Modules.Identity.AspNetCore.Tokens\|Themia.Modules.Identity.AspNetCore.Signing\|Themia.Modules.Identity.AspNetCore.Options" $S --include=*.cs | grep -v obj | grep -v bin
```
Then edit each listed staying file: replace `using Themia.Modules.Identity.AspNetCore.Tokens;` → `using Themia.Modules.Identity.Tokens.AspNetCore.Tokens;`, `.AspNetCore.Signing;` → `.Tokens.AspNetCore.Signing;`, and the JwtOptions import `using Themia.Modules.Identity.AspNetCore.Options;` → split: keep `…AspNetCore.Options` for any options that stayed (e.g. ExternalAuthOptions hasn't moved yet — it moves in Phase B) and add `using Themia.Modules.Identity.Tokens.AspNetCore.Options;` for `JwtOptions`. (If a file uses both an Options type that stays and `JwtOptions`, it needs both usings.)

> Note: `AuthTokenIssuer` is consumed by the staying local `AuthenticationFlow` and (Phase B) the external flow. With the Tokens package's `InternalsVisibleTo("Themia.Modules.Identity.AspNetCore")`, the local flow keeps calling it after updating its `using` to `Themia.Modules.Identity.Tokens.AspNetCore.Authentication`.

- [ ] **Step 5: Build the solution** — Run: `dotnet build Themia.sln --no-incremental`. Fix any remaining usings until 0 errors. Handle `Identity.AspNetCore` RS0017 (the moved types are no longer declared there): delete their lines from `$S/PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (they're now in the Tokens package's PublicAPI). Expected: 0/0.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: move JWT-issuance stack into Themia.Modules.Identity.Tokens.AspNetCore"
```

---

### Task 3: `AddThemiaIdentityTokens` DI extension + move token tests

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Tokens.AspNetCore/DependencyInjection/IdentityTokensServiceCollectionExtensions.cs`
- Create test project: `tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests/…`
- Move: the access-token + signing tests from `tests/Themia.Modules.Identity.AspNetCore.Tests/` into the new test project.

- [ ] **Step 1: Implement `AddThemiaIdentityTokens`** — extract the token-registration lines from the current `AddThemiaIdentityAspNetCore` (`IdentityAspNetCoreServiceCollectionExtensions.cs:49-60`): validate `JwtOptions`, register `JwtOptions`, `TimeProvider`, `IJwtSigningCredentialsProvider`→`SymmetricSigningCredentialsProvider`, `IAccessTokenService`→`AccessTokenService`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Themia.Modules.Identity.Tokens.AspNetCore.Tokens;

namespace Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;

/// <summary>Registers Themia's persistence-free JWT access-token issuance: validated <see cref="JwtOptions"/>,
/// the symmetric signing-credentials provider, and the default <see cref="IAccessTokenService"/>. Required by
/// the external-auth flow and the bundled Identity ASP.NET Core wiring; standalone-usable for
/// bring-your-own-user-store consumers.</summary>
public static class IdentityTokensServiceCollectionExtensions
{
    /// <summary>Adds JWT access-token issuance. All registrations use <c>TryAdd</c> so an adopter can replace
    /// any piece (e.g. a custom <see cref="IAccessTokenService"/> or signing provider) by registering it first.</summary>
    public static IServiceCollection AddThemiaIdentityTokens(
        this IServiceCollection services, Action<JwtOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new JwtOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJwtSigningCredentialsProvider, SymmetricSigningCredentialsProvider>();
        services.TryAddSingleton<IAccessTokenService, AccessTokenService>();
        return services;
    }
}
```

- [ ] **Step 2: Add the public extension to `PublicAPI.Unshipped.txt`** (build `--no-incremental` and add the exact RS0016 line).

- [ ] **Step 3: Create the test project** — copy `tests/Themia.Modules.Identity.AspNetCore.Tests/*.csproj` to `tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests.csproj`, change the name, set the single ProjectReference to the Tokens package, add to the sln (`dotnet sln Themia.sln add …`).

- [ ] **Step 4: Move the token/signing tests** — `git mv` the access-token + signing test files (e.g. `AccessTokenServiceTests.cs`, any `SymmetricSigningCredentialsProviderTests`/`JwtOptionsTests`) from `Identity.AspNetCore.Tests` to the new project; renamespace their `using`s to `Themia.Modules.Identity.Tokens.AspNetCore.*`.

- [ ] **Step 5: Add an `AddThemiaIdentityTokens` test** — `tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests/IdentityTokensServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Xunit;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Tests;

public class IdentityTokensServiceCollectionExtensionsTests
{
    [Fact]
    public void AddThemiaIdentityTokens_registers_IAccessTokenService()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityTokens(o => { /* set the minimum valid JwtOptions: copy a valid config from an existing AccessTokenService test */ });
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<IAccessTokenService>());
    }

    [Fact]
    public void AddThemiaIdentityTokens_throws_on_invalid_options()
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<Exception>(() => services.AddThemiaIdentityTokens(o => { /* leave required JwtOptions unset → Validate throws */ }));
    }
}
```
> Fill the valid `JwtOptions` config from an existing `AccessTokenServiceTests` setup (issuer/audience/signing key/lifetime). Do not invent field names — copy them.

- [ ] **Step 6: Run** — `dotnet test tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests` → all PASS; `dotnet build Themia.sln` → 0/0.

- [ ] **Step 7: Wire `AddThemiaIdentityAspNetCore` to call `AddThemiaIdentityTokens`** — in `IdentityAspNetCoreServiceCollectionExtensions.cs`, replace the inlined token registrations (the `JwtOptions` validate + `IJwtSigningCredentialsProvider`/`IAccessTokenService`/`TimeProvider` lines) with a single `services.AddThemiaIdentityTokens(configure);` call (add `using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;`). Keep the rest (guard, local flow, hooks, external flow lines for now). Build solution 0/0; `Identity.AspNetCore.Tests` still green.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add AddThemiaIdentityTokens and move token tests; delegate token registration"
```

---

# Phase B — `Themia.Modules.Identity.ExternalAuth.AspNetCore`

### Task 4: Scaffold the ExternalAuth package

**Files:**
- Create: `src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore/Themia.Modules.Identity.ExternalAuth.AspNetCore.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Identity.ExternalAuth.AspNetCore</PackageId>
    <Description>External OAuth/OIDC authentication for Themia Identity — server-side code→token exchange, PKCE, token-bound nonce, id-token validation, JWKS RS256 + HS256, and the external-login flow over a bring-your-own IExternalLoginService. No EF/Dapper, no user store.</Description>
    <PackageTags>themia;identity;oidc;oauth;external-login;authentication;aspnetcore</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
    <ProjectReference Include="../Themia.Modules.Identity.Tokens.AspNetCore/Themia.Modules.Identity.Tokens.AspNetCore.csproj" />
    <ProjectReference Include="../../neutral/Themia.AspNetCore/Themia.AspNetCore.csproj" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.Protocols.OpenIdConnect" />
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
    <InternalsVisibleTo Include="Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Seed PublicAPI files** (`#nullable enable` each). Add to sln: `dotnet sln Themia.sln add src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore/Themia.Modules.Identity.ExternalAuth.AspNetCore.csproj`. Build the empty project → succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore Themia.sln
git commit -m "feat: scaffold Themia.Modules.Identity.ExternalAuth.AspNetCore package"
```

---

### Task 5: Move the external-auth code (verbatim + renamespace)

**Files (move from `Themia.Modules.Identity.AspNetCore/` → `…ExternalAuth.AspNetCore/`):**
`External/OidcExternalAuthProvider.cs`, `External/OidcProviderConfig.cs`, `External/ExternalAuthProviderRegistry.cs`, `External/ExternalAuthenticationFlow.cs`, `External/ExternalAuthenticationHooksBase.cs`, `DependencyInjection/ExternalAuthBuilder.cs`, `Endpoints/IdentityExternalAuthEndpoints.cs`, `Options/ExternalAuthOptions.cs`.

- [ ] **Step 1: git mv the files**

```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
S=src/modules/Themia.Modules.Identity.AspNetCore
E=src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore
mkdir -p $E/External $E/DependencyInjection $E/Endpoints $E/Options
git mv $S/External/OidcExternalAuthProvider.cs $E/External/
git mv $S/External/OidcProviderConfig.cs $E/External/
git mv $S/External/ExternalAuthProviderRegistry.cs $E/External/
git mv $S/External/ExternalAuthenticationFlow.cs $E/External/
git mv $S/External/ExternalAuthenticationHooksBase.cs $E/External/
git mv $S/DependencyInjection/ExternalAuthBuilder.cs $E/DependencyInjection/
git mv $S/Endpoints/IdentityExternalAuthEndpoints.cs $E/Endpoints/
git mv $S/Options/ExternalAuthOptions.cs $E/Options/
```

- [ ] **Step 1b (plan amendment — discovered during execution): relocate `AuthResponse` to Abstractions.** `IdentityExternalAuthEndpoints.cs` returns `AuthResponse` (`public sealed record AuthResponse(string AccessToken, int ExpiresIn, string RefreshToken)`), which is declared in the STAYING `Endpoints/IdentityAuthEndpoints.cs` and also used by the staying local endpoints — so the moved endpoint can't reach it without a cycle. Move the `AuthResponse` record into `src/modules/Themia.Modules.Identity.Abstractions/Authentication/AuthenticationFlowContracts.cs` (next to its sibling `AuthTokens`), namespace `Themia.Modules.Identity.Abstractions.Authentication`. Both endpoint files then `using Themia.Modules.Identity.Abstractions.Authentication;` (they already do for `AuthTokens`). Update PublicAPI: remove `AuthResponse*` lines from `Identity.AspNetCore/PublicAPI.*.txt`, add them to `Abstractions/PublicAPI.Unshipped.txt`.

- [ ] **Step 2: Renamespace** — in every moved file, `namespace Themia.Modules.Identity.AspNetCore.<Sub>` → `Themia.Modules.Identity.ExternalAuth.AspNetCore.<Sub>`, and update intra-set usings (`…AspNetCore.External` → `…ExternalAuth.AspNetCore.External`, `…AspNetCore.Options` (ExternalAuthOptions) → `…ExternalAuth.AspNetCore.Options`, `…AspNetCore.DependencyInjection` → `…ExternalAuth.AspNetCore.DependencyInjection`). Update the `AuthTokenIssuer` using in `ExternalAuthenticationFlow.cs` to `Themia.Modules.Identity.Tokens.AspNetCore.Authentication`.

```bash
for f in $(git diff --cached --name-only | grep "^$E/"); do
  sed -i '' 's/namespace Themia\.Modules\.Identity\.AspNetCore\./namespace Themia.Modules.Identity.ExternalAuth.AspNetCore./' "$f"
  sed -i '' 's/using Themia\.Modules\.Identity\.AspNetCore\.External;/using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;/' "$f"
  sed -i '' 's/using Themia\.Modules\.Identity\.AspNetCore\.Options;/using Themia.Modules.Identity.ExternalAuth.AspNetCore.Options;/' "$f"
  sed -i '' 's/using Themia\.Modules\.Identity\.AspNetCore\.Authentication;/using Themia.Modules.Identity.Tokens.AspNetCore.Authentication;/' "$f"
done
```
> Verify no moved file still imports a `Themia.Modules.Identity.AspNetCore.*` namespace (it shouldn't — confirm with `grep -rn "Themia.Modules.Identity.AspNetCore" $E`). The only Themia usings should be `…Abstractions[.*]`, `…Tokens.AspNetCore.Authentication`, `Themia.AspNetCore.Exceptions`, and the package's own `…ExternalAuth.AspNetCore.*`.

- [ ] **Step 3: Build the ExternalAuth project** — `dotnet build $E/Themia.Modules.Identity.ExternalAuth.AspNetCore.csproj --no-incremental`. Add any missing PackageReferences (e.g. `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions`) flagged. Add all RS0016 public lines to `$E/PublicAPI.Unshipped.txt`.

- [ ] **Step 4: Re-point `Identity.AspNetCore`** — add the ProjectReference: `dotnet add $S/Themia.Modules.Identity.AspNetCore.csproj reference $E/Themia.Modules.Identity.ExternalAuth.AspNetCore.csproj`. Update `IdentityAspNetCoreServiceCollectionExtensions.cs`: change the external-flow registration lines' type references (`External.ExternalAuthenticationFlow` / `External.ExternalAuthenticationHooksBase`) — these move to `AddThemiaExternalAuth` in Task 6, so for now add `using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;` to keep it compiling, OR proceed straight to Task 6 (recommended — they're one change). Handle `Identity.AspNetCore` RS0017: remove the moved external-auth type lines from its `PublicAPI.Shipped.txt`/`Unshipped.txt`.

- [ ] **Step 5: Build solution** — `dotnet build Themia.sln --no-incremental` → 0/0 (with the temporary using from Step 4). Commit:

```bash
git add -A
git commit -m "refactor: move external-auth code into Themia.Modules.Identity.ExternalAuth.AspNetCore"
```

---

### Task 6: Re-home flow/hooks registration into `AddThemiaExternalAuth` with the external-only guard

**Files:**
- Modify: `src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore/DependencyInjection/ExternalAuthBuilder.cs` (the `ExternalAuthServiceCollectionExtensions.AddThemiaExternalAuth` method)
- Modify: `src/modules/Themia.Modules.Identity.AspNetCore/DependencyInjection/IdentityAspNetCoreServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the failing test** — `tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests/ExternalAuthRegistrationTests.cs` (create the test project first — see Task 7 Step 1; if doing this task first, scaffold the test csproj now):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests;

public class ExternalAuthRegistrationTests
{
    [Fact]
    public void AddThemiaExternalAuth_registers_the_flow()
    {
        var services = new ServiceCollection();
        services.AddThemiaExternalAuth();
        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthenticationFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthenticationHooks));
        Assert.Contains(services, d => d.ServiceType == typeof(IExternalAuthProviderRegistry));
    }

    [Fact]
    public void External_guard_requires_login_and_token_seams_not_user_service()
    {
        // Build a provider with the external seams present but NO IUserService, then run the guard.
        var services = new ServiceCollection();
        services.AddThemiaExternalAuth();
        services.AddScoped<IExternalLoginService>(_ => new StubExternalLoginService());
        services.AddScoped<IRefreshTokenService>(_ => new StubRefreshTokenService());
        services.AddScoped<IClaimsPrincipalFactory>(_ => new StubClaimsPrincipalFactory());
        services.AddThemiaIdentityTokens(o => { /* valid JwtOptions */ }); // supplies IAccessTokenService
        var ex = Record.Exception(() => services.ValidateThemiaExternalAuth()); // the guard, callable explicitly
        Assert.Null(ex);
    }

    [Fact]
    public void External_guard_throws_without_login_service_and_message_excludes_IUserService()
    {
        var services = new ServiceCollection();
        services.AddThemiaExternalAuth();
        var ex = Assert.Throws<InvalidOperationException>(() => services.ValidateThemiaExternalAuth());
        Assert.DoesNotContain("IUserService", ex.Message);
        Assert.Contains("IExternalLoginService", ex.Message);
    }
}
```
> Define minimal stub implementations (`StubExternalLoginService`, `StubRefreshTokenService`, `StubClaimsPrincipalFactory`) in the test file from the real interface signatures in `Themia.Modules.Identity.Abstractions.Authentication`.

- [ ] **Step 2: Run it** — Expected: FAIL (flow not registered by `AddThemiaExternalAuth`; `ValidateThemiaExternalAuth` doesn't exist).

- [ ] **Step 3: Re-home the registration** — in `ExternalAuthServiceCollectionExtensions.AddThemiaExternalAuth`, after the existing registry/HttpClient/TimeProvider registrations, add the flow + hooks (moved out of `AddThemiaIdentityAspNetCore`):

```csharp
        services.TryAddScoped<IExternalAuthenticationFlow, External.ExternalAuthenticationFlow>();
        services.TryAddScoped<IExternalAuthenticationHooks, External.ExternalAuthenticationHooksBase>();
```
Add a `using Themia.Modules.Identity.Abstractions.Authentication;` for the interfaces. Then add the external-only guard as a separate, explicitly-callable extension (so it can be unit-tested and is invoked by the bundled wiring + at app build):

```csharp
    /// <summary>Fail-fast check that the external-login flow's runtime dependencies are registered:
    /// <see cref="IExternalLoginService"/> (the bring-your-own user-store seam), and the token seams
    /// <see cref="IAccessTokenService"/> / <see cref="IRefreshTokenService"/> / <see cref="IClaimsPrincipalFactory"/>.
    /// Deliberately does NOT require <c>IUserService</c> — the external flow never uses it.</summary>
    public static IServiceCollection ValidateThemiaExternalAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        static bool Has(IServiceCollection s, Type t) => s.Any(d => d.ServiceType == t);
        var missing = new List<string>();
        if (!Has(services, typeof(IExternalLoginService))) missing.Add(nameof(IExternalLoginService));
        if (!Has(services, typeof(IAccessTokenService))) missing.Add(nameof(IAccessTokenService));
        if (!Has(services, typeof(IRefreshTokenService))) missing.Add(nameof(IRefreshTokenService));
        if (!Has(services, typeof(IClaimsPrincipalFactory))) missing.Add(nameof(IClaimsPrincipalFactory));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Themia external auth requires these services to be registered: " + string.Join(", ", missing) +
                ". Register your IExternalLoginService (and IAccessTokenService via AddThemiaIdentityTokens, " +
                "plus IRefreshTokenService / IClaimsPrincipalFactory).");
        }

        return services;
    }
```
> `ValidateThemiaExternalAuth` requires `using System.Linq;`. Keep it a separate call (not inside `AddThemiaExternalAuth`) because the seams are typically registered after the provider builder; callers invoke it once wiring is complete (the bundled path calls it; ezy can call it in tests/startup).

- [ ] **Step 4: Remove the external-flow registration from `AddThemiaIdentityAspNetCore`** — delete the two `services.TryAddScoped<IExternalAuthenticationFlow,…>` / `IExternalAuthenticationHooks` lines (now in `AddThemiaExternalAuth`). Keep the local-flow guard (`IUserService`/etc.) and local `AuthenticationFlow`/`AuthenticationHooks` + the JwtBearer scheme. Update its `using`s (drop the now-unused `External` import). Bundled consumers already call `AddThemiaExternalAuth()`, so the external flow is still registered for them.

- [ ] **Step 5: Run** — `dotnet test tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests --filter ExternalAuthRegistrationTests` → PASS; `dotnet build Themia.sln` → 0/0. Add the new public `ValidateThemiaExternalAuth` to `$E/PublicAPI.Unshipped.txt`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: re-home external-flow registration into AddThemiaExternalAuth with external-only guard"
```

---

### Task 7: Move OIDC tests + add the BYO end-to-end test

**Files:**
- Create test project: `tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests/…` (if not created in Task 6)
- Move: the external-auth tests (`OidcExternalAuthProviderTests.cs` + any `ExternalAuthenticationFlow`/registry tests) from `Identity.AspNetCore.Tests`.
- Create: `tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests/ByoExternalLoginTests.cs`

- [ ] **Step 1: Create the test csproj** — copy `Identity.AspNetCore.Tests` csproj; name it `Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests`; ProjectReference the ExternalAuth package + the Tokens package (for `AddThemiaIdentityTokens`) + `Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`, already pinned). Add to sln.

- [ ] **Step 2: Move the external-auth tests** — `git mv tests/Themia.Modules.Identity.AspNetCore.Tests/External/OidcExternalAuthProviderTests.cs tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests/` (+ any sibling external test files + shared `TestIdTokens`/`StubHttpMessageHandler` helpers they use — move the helpers too, or duplicate if also used by staying tests; grep to confirm). Renamespace test `using`s to `Themia.Modules.Identity.ExternalAuth.AspNetCore.*` and `…Tokens.AspNetCore.*`.

- [ ] **Step 3: Run the moved tests** — `dotnet test tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests --filter Oidc` → PASS (the ~24 OIDC tests, both behaviors incl. rotation).

- [ ] **Step 4: Write the BYO end-to-end test** — proves the flow works with NO persistence / NO `IUserService`:

```csharp
[Fact]
public async Task External_login_succeeds_with_byo_seams_and_no_identity_persistence()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddThemiaIdentityTokens(o => { /* valid JwtOptions */ });
    services.AddThemiaExternalAuth().AddProvider(new FakeExternalAuthProvider("test", ExternalIdentityFor("u-1")));
    services.AddScoped<IExternalLoginService>(_ => new StubExternalLoginService());      // returns a resolved User for the ExternalIdentity
    services.AddScoped<IRefreshTokenService>(_ => new StubRefreshTokenService());
    services.AddScoped<IClaimsPrincipalFactory>(_ => new StubClaimsPrincipalFactory());
    // NOTE: no AddThemiaIdentity, no IUserService.
    services.ValidateThemiaExternalAuth();
    using var sp = services.BuildServiceProvider();

    using var scope = sp.CreateScope();
    var flow = scope.ServiceProvider.GetRequiredService<IExternalAuthenticationFlow>();
    var result = await flow.AuthenticateAsync("test", new ExternalAuthRequest(/* code/redirect/etc. */), TestContextCt());

    Assert.True(result.Succeeded);
    Assert.False(string.IsNullOrEmpty(result.AccessToken)); // issued via the defaulted IAccessTokenService
}
```
> Build `FakeExternalAuthProvider` (implements `IExternalAuthProvider`, returns a fixed validated `ExternalIdentity`), and the three stubs, from the real Abstractions signatures. Copy the exact `ExternalAuthRequest`/`ExternalLoginFlowResult`/`IExternalLoginService.ResolveOrProvisionAsync` shapes from the moved `ExternalAuthenticationFlow` + the Abstractions contracts — do not invent members.

- [ ] **Step 5: Run** — `dotnet test tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests` → all PASS; `dotnet build Themia.sln` → 0/0.

- [ ] **Step 6: Confirm `Identity.AspNetCore.Tests` still green** — `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests` → PASS (local/password + JwtBearer; bundled external flow still wired via the re-reference). If a moved shared helper broke it, restore a copy in that project.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "test: move OIDC tests to ExternalAuth package and add the BYO no-persistence flow test"
```

---

# Phase C — Release

### Task 8: Version, CHANGELOG, MIGRATION, full verification

**Files:** `Directory.Build.props`, `CHANGELOG.md`, `MIGRATION.md`

- [ ] **Step 1: Bump version** — `Directory.Build.props` `<Version>0.6.5</Version>` → `0.6.6`.

- [ ] **Step 2: CHANGELOG** — add `## [0.6.6] - 2026-06-23` with:
  - **Added** — `Themia.Modules.Identity.Tokens.AspNetCore` (persistence-free JWT access-token issuance: `AccessTokenService`, symmetric signing, `JwtOptions`, the shared `AuthTokenIssuer`, `AddThemiaIdentityTokens`) and `Themia.Modules.Identity.ExternalAuth.AspNetCore` (external OAuth/OIDC: provider + registry + `AddThemiaExternalAuth` builder + the external-login flow/hooks + `MapIdentityExternalAuthEndpoints`, over a bring-your-own `IExternalLoginService`; external-only DI guard that does not require `IUserService`). Both depend only on `Themia.Modules.Identity.Abstractions`.
  - **Changed (breaking)** — the external-auth and JWT-issuance types moved out of `Themia.Modules.Identity.AspNetCore` (namespace change); see MIGRATION. Bundled consumers update `using` directives only.

- [ ] **Step 3: MIGRATION** — add `## 0.6.6` with the old→new namespace map (every moved public type: `…AspNetCore.Tokens/.Signing/.Options.JwtOptions` → `…Tokens.AspNetCore.*`; `…AspNetCore.External/.Endpoints/.Options.ExternalAuthOptions/.DependencyInjection.ExternalAuth*` → `…ExternalAuth.AspNetCore.*`), the bundled-consumer note (re-references keep types available; only `using`s change), and the BYO adoption path (call `AddThemiaIdentityTokens` + `AddThemiaExternalAuth`, register `IExternalLoginService` + `IRefreshTokenService` + `IClaimsPrincipalFactory`; `IAccessTokenService` is defaulted by the Tokens package; or use the provider/registry directly for a validated `ExternalIdentity`).

- [ ] **Step 4: Full build + test** — `dotnet build Themia.sln --no-incremental` → 0 warnings / 0 errors (surfaces any RS0016/RS0017 across all three packages); `dotnet test Themia.sln --filter Category!=Integration` → all unit suites green. (Integration suites are CI-gated.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: bump to 0.6.6 and document the external-auth + token package extraction"
```

---

## Self-Review

**Spec coverage:**
- Tokens package (JWT issuance, persistence-free) → Tasks 1–3. ✅
- ExternalAuth package (protocol + flow + builder + endpoints) → Tasks 4–5. ✅
- Re-homed flow registration + external-only guard (no `IUserService`) → Task 6. ✅
- `AuthTokenIssuer` internal + `InternalsVisibleTo` both consumers → Task 1 Step 2 + Task 2. ✅
- `Identity.AspNetCore` re-references both, bundled behavior preserved → Tasks 2/5/6. ✅
- BYO path (supply `IExternalLoginService`/`IRefreshTokenService`/`IClaimsPrincipalFactory`; `IAccessTokenService` defaulted) → Task 7 BYO test + guard. ✅
- Breaking namespace move + PublicAPI RS0017 handling → Tasks 2/5 (remove lines) + Task 8 MIGRATION. ✅
- Move tests to two new projects + keep `Identity.AspNetCore.Tests` green → Tasks 3/7. ✅
- Version 0.6.6 + CHANGELOG + MIGRATION → Task 8. ✅

**Placeholder scan:** the `JwtOptions` valid-config and the stub/fake shapes are intentionally "copy from the existing tests / real interfaces" rather than invented signatures — the engineer must read the actual `JwtOptions`, `IExternalLoginService`, `ExternalAuthRequest`, `ExternalLoginFlowResult` members. This is deliberate (inventing member names would be wrong), but flag: if a member name is unclear, grep the moved source before writing the stub. No TBD/TODO left.

**Type consistency:** `AddThemiaIdentityTokens`, `AddThemiaExternalAuth`, `ValidateThemiaExternalAuth`, `IExternalAuthenticationFlow`, `IExternalLoginService`, `IAccessTokenService`/`IRefreshTokenService`/`IClaimsPrincipalFactory`, `AuthTokenIssuer` are used consistently across tasks. The Tokens namespace is `Themia.Modules.Identity.Tokens.AspNetCore.*`; the ExternalAuth namespace is `Themia.Modules.Identity.ExternalAuth.AspNetCore.*` throughout.

**One sequencing note:** Task 6's tests need the ExternalAuth test project; if executing strictly in order, scaffold that test csproj (Task 7 Step 1) before Task 6 Step 1, or fold Task 7 Step 1 into Task 6.
