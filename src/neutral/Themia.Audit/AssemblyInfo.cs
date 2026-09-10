using System.Runtime.CompilerServices;

// Grants Themia.Audit.AspNetCore.Tests access to AuditEntry.Data's internal init accessor so its fakes
// can build fixtures that look like a row IAuditStore.GetAsync/QueryAsync actually returned (Data already
// populated) — the same shape AuditStoreEngine produces from within this assembly. Production callers
// outside this friend list still cannot set Data; the "recorder serializes, callers never supply a
// string" invariant (design §8) is unaffected.
[assembly: InternalsVisibleTo("Themia.Audit.AspNetCore.Tests")]

// Grants the three dialect packages access to AuditMigrationHandshake so their AddThemiaAudit{Engine}()
// calls can record the other half of the order-free runMigration handshake (design §5.2). Production
// friends only — no test project needs this, since the handshake is exercised through the public
// AddThemiaAudit / AddThemiaAudit{Engine} API.
[assembly: InternalsVisibleTo("Themia.Audit.PostgreSql")]
[assembly: InternalsVisibleTo("Themia.Audit.MySql")]
[assembly: InternalsVisibleTo("Themia.Audit.SqlServer")]
