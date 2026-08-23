# Release notes — 0.17.0

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

**Known limitation — a non-default `search_path` can still fail inside a migration.**
`Themia.Challenges` and `Themia.Modules.Messaging` each create their tables with fluent
`Create.Table(...)` (forced to `public` by FluentMigrator) but create some of their indexes with
raw `Execute.Sql(...)` (which follows `search_path`). On a non-default `search_path` these two
migrations are internally inconsistent and throw during migration — before the new probe ever
gets to run. So the probe does not fully close the gap for these two stores: a *first* boot on a
non-default `search_path` still dies, just inside the migration instead of on first use, with a
less specific error than the probe gives. The migrations are unchanged in this release; this is
tracked as a known limitation, not a regression.
