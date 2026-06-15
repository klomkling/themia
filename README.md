# Themia

A **.NET 10** application framework — a **framework core** plus a catalog of **pluggable modules**
(`IThemiaModule`). Its cross-cutting **neutral** packages additionally target **.NET 8**, so
non-Themia apps (e.g. a net8 Serenity app) can consume them. All packages ship under the
`Themia.*` NuGet prefix.

> **Status:** active development, released through **`0.5.1`**. Shipped so far: the build-time
> **tooling** (`Themia.SourceGenerator`, `Themia.Analyzers`, `Themia.Generators.Abstractions`);
> the **framework core** (`Themia.Framework.Core`, `Themia.Caching`, `Themia.Logging`,
> `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Services`, `Themia.Framework.AspNetCore`); a
> **selectable data layer** with EF Core and Dapper as first-class peers
> (`Themia.Framework.Data.*`) over a FluentMigrator-owned schema (`Themia.Data.Migrations`); the
> **neutral cross-cutting** packages (`Themia.AspNetCore`, `Themia.DependencyInjection`,
> `Themia.Quartz`, `Themia.Exceptional.*`); and the first **modules**
> (`Themia.Modules.Scheduling` with persistent Quartz, `Themia.Modules.Identity` tenant-aware
> Identity core, `Themia.Modules.Identity.AspNetCore` JWT + rotating refresh tokens). The
> architecture overview, module specs, and implementation plans live under
> [`docs/`](docs/); the full release history is in [CHANGELOG.md](CHANGELOG.md).

## Architecture

Layered, with a strict downward dependency direction:

| Layer | Target frameworks | Packages |
|---|---|---|
| **Tooling** (build-time) | `netstandard2.0` | `Themia.SourceGenerator`, `Themia.Analyzers`, `Themia.Generators.Abstractions` |
| **Framework core** | `net10.0` | `Themia.Framework.Core`, `Themia.Framework.AspNetCore`, `Themia.Framework.Data.Abstractions` / `.EFCore(.SqlServer/.PostgreSql)` / `.Dapper(.SqlServer/.MySql/.PostgreSql)`, `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Caching`, `Themia.Logging`, `Themia.Services` |
| **Neutral cores** | `net8.0;net10.0` | `Themia.DependencyInjection`, `Themia.Data.Migrations`, `Themia.Quartz`, `Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)`, `Themia.AspNetCore` |
| **Modules** | `net10.0` | `Themia.Modules.Scheduling`, `Themia.Modules.Identity(.Abstractions)`, `Themia.Modules.Identity.AspNetCore`, … (ExceptionLogging, Storage planned) |

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

## Building

Requires the .NET 10 SDK (with the .NET 8 runtime available for the multi-targeted packages).

```bash
dotnet build Themia.sln     # builds all TFMs (net8.0 + net10.0)
dotnet test Themia.sln
```

## Documentation

- [Architecture overview & module catalog](docs/themia-architecture-overview.md)
- [Dapper data-layer design](docs/superpowers/specs/2026-06-07-themia-dapper-data-layer-design.md)
- [Identity core design](docs/superpowers/specs/2026-06-14-themia-identity-core-design.md)
- [Identity JWT design (0.5.1)](docs/superpowers/specs/2026-06-15-themia-identity-jwt-design.md)
- [Scheduling (Quartz) design](docs/superpowers/specs/2026-06-01-themia-quartz-scheduling-design.md)
- [Exception logging design](docs/superpowers/specs/2026-06-01-themia-exceptional-design.md)
- [Release strategy & CHANGELOG conventions](docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md)
- [`Themia.AspNetCore` implementation plan](docs/superpowers/plans/2026-06-01-themia-aspnetcore.md)
