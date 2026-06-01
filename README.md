# Themia

A .NET 8/10 application framework: a **framework core** plus a catalog of **pluggable modules**
(`IThemiaModule`). All packages ship under the `Themia.*` NuGet prefix.

> **Status:** design phase. This repository currently holds the architecture overview, module
> specs, and implementation plans under [`docs/`](docs/). The first package to be implemented is
> `Themia.AspNetCore`. No solution or code has landed yet.

## Architecture

Layered, with a strict downward dependency direction:

| Layer | Target frameworks | Packages |
|---|---|---|
| **Tooling** (build-time) | `netstandard2.0` | `Themia.SourceGenerator`, `Themia.Analyzers(.CodeFixes)`, `Themia.Generators.Abstractions` |
| **Framework core** | `net10.0` | `Themia.Framework.Core` / `.Data.EFCore` / `.AspNetCore`, `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Caching`, `Themia.Logging`, `Themia.Services` |
| **Neutral cores** | `net8.0;net10.0` | `Themia.Quartz`, `Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)`, `Themia.AspNetCore` |
| **Modules** | `net10.0` | `Themia.Modules.*` (Scheduling, ExceptionLogging, Identity, Storage, …) |

Two rules drive the design:

1. **Framework / app boundary** — only framework + cross-cutting infrastructure lives in Themia.
   Business domains stay in their own apps.
2. **Three-layer pattern** for each cross-cutting concern — neutral core (`Themia.X`, no framework
   dependency) → Themia module (`Themia.Modules.X`) → optional, deferred Serenity adapter.

See [`docs/themia-architecture-overview.md`](docs/themia-architecture-overview.md) for the full
picture, decisions, and build order.

## Multi-database support

Phase 1 targets **SQL Server, MySQL (incl. MariaDB), and PostgreSQL** via a dialect strategy and
per-provider packages.

## Building

Requires the .NET 10 SDK (with the .NET 8 runtime available for the multi-targeted packages).

```bash
dotnet build Themia.sln     # once the solution is scaffolded
dotnet test Themia.sln
```

## Documentation

- [Architecture overview & module catalog](docs/themia-architecture-overview.md)
- [Scheduling (Quartz dashboard) design](docs/superpowers/specs/2026-06-01-themia-quartz-scheduling-design.md)
- [Exception logging design](docs/superpowers/specs/2026-06-01-themia-exceptional-design.md)
- [`Themia.AspNetCore` implementation plan](docs/superpowers/plans/2026-06-01-themia-aspnetcore.md)
