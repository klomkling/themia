# THEMIA104 — DbSet.Find bypasses the tenant post-check

**Category:** Themia.Isolation · **Severity:** Warning

`DbSet<T>.Find` / `FindAsync` returns an already-tracked entity straight from EF's identity map **without**
re-applying `ThemiaDbContext`'s tenant / soft-delete post-check. If a row from another tenant is already
tracked in the context, `DbSet.Find` will return it.

**Do this instead:** use `DbContext.FindAsync<T>()` (Themia's guarded override) or
`IReadRepository.GetByIdAsync()` — both re-validate tenant access even for tracked entities.

**Deliberate bypass:** suppress with a justification — `#pragma warning disable THEMIA104` or
`[SuppressMessage("Themia.Isolation", "THEMIA104", Justification = "…")]`.
