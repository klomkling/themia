# Themia

A **.NET 10** application framework — a **framework core** plus a catalog of **pluggable modules**
(`IThemiaModule`). Its cross-cutting **neutral** packages additionally target **.NET 8**, so
non-Themia apps (e.g. a net8 Serenity app) can consume them. All packages ship under the
`Themia.*` NuGet prefix.

> **Status:** active development, released through **`0.5.3`**. Shipped so far: the build-time
> **tooling** (`Themia.SourceGenerator`, `Themia.Analyzers`, `Themia.Generators.Abstractions`);
> the **framework core** (`Themia.Framework.Core`, `Themia.Caching`, `Themia.Logging`,
> `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Services`, `Themia.Framework.AspNetCore`); a
> **selectable data layer** with EF Core and Dapper as first-class peers
> (`Themia.Framework.Data.*`) over a FluentMigrator-owned schema (`Themia.Data.Migrations`); the
> **neutral cross-cutting** packages (`Themia.AspNetCore`, `Themia.DependencyInjection`,
> `Themia.Quartz`, `Themia.Exceptional.*`); and the first **modules**
> (`Themia.Modules.Scheduling` with persistent Quartz, `Themia.Modules.Identity` tenant-aware
> Identity core, `Themia.Modules.Identity.AspNetCore` JWT + rotating refresh tokens + pluggable
> external/OAuth login), and `Themia.Modules.Storage` (tenant-aware object storage over Local +
> S3/R2 backends). The
> architecture overview, module specs, and implementation plans live under
> [`docs/`](docs/); the full release history is in [CHANGELOG.md](CHANGELOG.md).

## Architecture

Layered, with a strict downward dependency direction:

| Layer | Target frameworks | Packages |
|---|---|---|
| **Tooling** (build-time) | `netstandard2.0` | `Themia.SourceGenerator`, `Themia.Analyzers`, `Themia.Generators.Abstractions` |
| **Framework core** | `net10.0` | `Themia.Framework.Core`, `Themia.Framework.AspNetCore`, `Themia.Framework.Data.Abstractions` / `.EFCore(.SqlServer/.PostgreSql)` / `.Dapper(.SqlServer/.MySql/.PostgreSql)`, `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Caching`, `Themia.Logging`, `Themia.Services` |
| **Neutral cores** | `net8.0;net10.0` | `Themia.DependencyInjection`, `Themia.Data.Migrations`, `Themia.Quartz`, `Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)`, `Themia.AspNetCore` |
| **Modules** | `net10.0` | `Themia.Modules.Scheduling`, `Themia.Modules.Identity(.Abstractions)`, `Themia.Modules.Identity.AspNetCore`, `Themia.Modules.Storage`, … (ExceptionLogging planned) |

Two rules drive the design:

1. **Framework / app boundary** — only framework + cross-cutting infrastructure lives in Themia.
   Business domains stay in their own apps.
2. **Three-layer pattern** for each cross-cutting concern — neutral core (`Themia.X`, no framework
   dependency) → Themia module (`Themia.Modules.X`) → optional, deferred Serenity adapter.

See [`docs/themia-architecture-overview.md`](docs/themia-architecture-overview.md) for the full
picture, decisions, and build order.

## Data layer & multi-database support

EF Core (`Themia.Framework.Data.EFCore`) and Dapper (`Themia.Framework.Data.Dapper`) are
**selectable first-class peers** — an adopter picks one and the whole system runs on it; both
enforce tenant isolation, audit, soft-delete, and unit-of-work over the **same schema**, which is
owned by FluentMigrator (`Themia.Data.Migrations`) as the single authority for both layers.

Phase 1 targets **SQL Server, MySQL (incl. MariaDB), and PostgreSQL** via a dialect strategy and
per-provider packages.

## Which packages do I reference?

Themia ships many small packages on purpose — native DB drivers (SqlClient/Npgsql/MySqlConnector),
heavy optional dependencies (AWS SDK, ClosedXML), and the EF-vs-Dapper data-peer choice are kept in
separate packages so your app only pulls what it actually uses. You never assemble them by hand:

### Quickstart (multi-tenant web app)

Two references:

1. `Themia.Framework` — the framework core set (Core, Logging, Caching, Services, MultiTenancy,
   Mediator, Data.Abstractions, ASP.NET Core integration) in one metapackage.
2. Exactly one data peer+provider package — your conscious choice, never made for you:
   - `Themia.Framework.Data.EFCore.SqlServer` or `Themia.Framework.Data.EFCore.PostgreSql`
   - `Themia.Framework.Data.Dapper.SqlServer`, `Themia.Framework.Data.Dapper.PostgreSql`, or
     `Themia.Framework.Data.Dapper.MySql`
   - (No `EFCore.MySql` yet — there is no EF Core 10 MySQL provider.)

Then bootstrap: `services.AddThemiaAspNetCore()` + `services.AddThemiaMultiTenancy(...)` +
`app.UseThemia()` (see the smoke test in `tests/Themia.Framework.Tests/MetaPackageBootstrapTests.cs`
for a minimal working host).

Non-web apps (workers, consoles): skip the metapackage — it includes the ASP.NET Core integration —
and reference the individual `Themia.*` packages you need instead.

### Adding features

| Scenario | Add |
|---|---|
| Identity (users/roles/JWT/external login) | `Themia.Modules.Identity.AspNetCore` (umbrella — pulls the Identity core, tokens, and external-auth packages) |
| Scheduling (persistent Quartz) | `Themia.Modules.Scheduling` |
| Storage (incl. S3) | `Themia.Modules.Storage` |
| Async/scheduled export (CSV + xlsx) | `Themia.Modules.Export` — heaviest umbrella: also pulls the Storage, Scheduling, and Notifications modules |
| PDF templates + rendering | `Themia.Modules.Pdf` |
| Exception logging + dashboard | `Themia.Exceptional` + one `Themia.Exceptional.{SqlServer\|PostgreSql\|MySql}` + `Themia.Exceptional.AspNetCore` |
| Notifications | `Themia.Modules.Notifications` + one `Themia.Modules.Notifications.{SqlServer\|PostgreSql\|MySql}` |

> **Peer-coupling caveat:** some modules (`Identity`, `Storage`, `Notifications`, `Export`)
> currently reference both the EF Core and Dapper data peers (tracked follow-up,
> `docs/2026-06-14-identity-followups.md`), so adding them pulls both stacks regardless of the
> peer you chose. The metapackage itself never does.

## Building

Requires the .NET 10 SDK (with the .NET 8 runtime available for the multi-targeted packages).

```bash
dotnet build Themia.sln     # builds all TFMs (net8.0 + net10.0)
dotnet test Themia.sln
```

## Identity: external / OAuth login

`Themia.Modules.Identity.AspNetCore` adds a pluggable external/OAuth login system that issues the
**same** `AuthResponse` as password login. Register the providers you need, then map the endpoints:

```csharp
builder.Services
    .AddThemiaExternalAuth()
    .AddGoogle(o => { o.ClientId = "…"; o.ClientSecret = "…"; })   // standard OIDC
    .AddLine(o => { o.ChannelId = "…"; o.ChannelSecret = "…"; });  // OIDC-ish, HS256 channel secret

// opt-in endpoints, grouped under your auth prefix
app.MapGroup("/auth").MapIdentityExternalAuthEndpoints();
```

The flow is **headless**: the SPA/native client runs the provider authorization step itself, owns
the `state` value (CSRF), and posts the returned authorization code to the server for exchange.

```
POST /auth/external/{provider}
{ "code": "…", "redirectUri": "https://app.example/callback", "codeVerifier": "…" }  // codeVerifier optional (PKCE)

→ 200 AuthResponse { accessToken, expiresIn, refreshToken }   // refresh via POST /auth/refresh
→ 401 on any provider failure (uniform)   → 404 only for an unknown provider
```

The server performs the code→token exchange server-side (the client secret is never exposed or
logged), validates the id-token issuer / audience / signature / expiry, forwards the PKCE
`code_verifier`, and binds the `nonce` to the id-token (if the token carries one, a matching client
nonce is required, and vice-versa). Account resolution: an existing external link → auto-link to a
user **only when the provider asserts a verified email and the account is active** (an unverified
email never links and is never persisted; a deactivated/locked account is never auto-linked) →
otherwise provision a password-less user.
Google ships as standard OIDC and LINE as an HS256 channel-secret provider; Facebook / Microsoft /
Telegram are deferred additive providers. LINE emits no `email_verified` claim, so by default a LINE
login never auto-links to an existing account (opt in via `LineOptions.EmailAlwaysVerified` if you
trust LINE's email verification).

## Storage

`Themia.Modules.Storage` adds tenant-aware object storage over a Local filesystem or S3/R2 backend.
Every object's physical key is prefixed with the ambient tenant, so isolation holds by
construction; object metadata and a per-tenant quota are tracked in the `storage.storage_objects`
table (FluentMigrator schema, PostgreSQL + SQL Server) on either the EF Core or Dapper data peer.

```csharp
builder.Services
    .AddThemiaStorage()
    .UseLocal(o => { o.RootPath = "/var/themia/blobs"; o.SigningKey = "…"; })  // or .UseS3(…) / .UseR2(…)
    .AddValidation()   // size + content-type checks via IFileValidator
    .AddScanning();    // DI-replaceable IFileScanner seam (NullFileScanner by default)

// opt-in presigned-direct upload/download endpoints
app.MapThemiaStorageEndpoints();
```

Uploads are size- and content-type-validated before a write; the opt-in `MapThemiaStorageEndpoints`
flow issues presigned URLs so clients transfer bytes straight to/from the backend, keeping object
content off the application server. Secrets, credentials, and presigned URLs are never logged.

## Documentation

- [Architecture overview & module catalog](docs/themia-architecture-overview.md)
- [Dapper data-layer design](docs/superpowers/specs/2026-06-07-themia-dapper-data-layer-design.md)
- [Identity core design](docs/superpowers/specs/2026-06-14-themia-identity-core-design.md)
- [Identity JWT design (0.5.1)](docs/superpowers/specs/2026-06-15-themia-identity-jwt-design.md)
- [Identity external/OAuth login design (0.5.2)](docs/superpowers/specs/2026-06-16-themia-identity-external-login-design.md)
- [Storage design (0.5.3)](docs/superpowers/specs/2026-06-17-themia-storage-design.md) · [implementation plan](docs/superpowers/plans/2026-06-17-themia-storage-0.5.3.md)
- [Scheduling (Quartz) design](docs/superpowers/specs/2026-06-01-themia-quartz-scheduling-design.md)
- [Exception logging design](docs/superpowers/specs/2026-06-01-themia-exceptional-design.md)
- [Release strategy & CHANGELOG conventions](docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md)
- [`Themia.AspNetCore` implementation plan](docs/superpowers/plans/2026-06-01-themia-aspnetcore.md)
