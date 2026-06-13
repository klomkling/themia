# THEMIA103 — Raw Dapper connection bypasses tenant isolation

**Category:** Themia.Isolation · **Severity:** Warning

`IDapperConnectionContext.GetOpenConnectionAsync` returns the raw, unscoped database connection. Queries
issued on it do not carry the tenant predicate or soft-delete filter, so they can read or write across
tenants — the isolation guarantee holds only when access flows through the repositories.

**Do this instead:** use `ITenantQueryFactory.For<T>()` for ad-hoc tenant-aware queries (it pre-seeds the
tenant predicate + soft-delete filter), or the repositories / unit of work for reads and writes.

**Deliberate bypass:** suppress with a justification — `#pragma warning disable THEMIA103` or
`[SuppressMessage("Themia.Isolation", "THEMIA103", Justification = "…")]`. The suppression makes the
bypass conspicuous and reviewable in the diff.
