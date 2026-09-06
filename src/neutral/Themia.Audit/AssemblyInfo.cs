using System.Runtime.CompilerServices;

// Grants Themia.Audit.AspNetCore.Tests access to AuditEntry.Data's internal init accessor so its fakes
// can build fixtures that look like a row IAuditStore.GetAsync/QueryAsync actually returned (Data already
// populated) — the same shape AuditStoreEngine produces from within this assembly. Production callers
// outside this friend list still cannot set Data; the "recorder serializes, callers never supply a
// string" invariant (design §8) is unaffected.
[assembly: InternalsVisibleTo("Themia.Audit.AspNetCore.Tests")]
