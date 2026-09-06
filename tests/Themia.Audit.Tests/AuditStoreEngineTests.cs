using System.Data.Common;
using Dapper;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditStoreEngineTests
{
    // Regression guard for the process-wide-Dapper-registry defect: an earlier fix round made
    // AuditStoreEngine correct for MySQL's occurred_at round-trip by registering
    // SqlMapper.AddTypeHandler(new OccurredAtTypeHandler()) in a static constructor. That mutates
    // Dapper's GLOBAL type-handler registry — every DateTimeOffset materialization anywhere else in the
    // same process (an adopter's own Dapper repositories included) would then be silently reinterpreted,
    // relabelling a genuinely local Kind=Unspecified value as UTC. AuditStoreEngine must fix the MySQL bug
    // (see AuditStoreTestsBase.Every_field_lands_in_its_own_column, run per engine in
    // Themia.Audit.IntegrationTests) WITHOUT touching Dapper's global registry to do it.
    [Fact]
    public void Does_not_register_a_process_wide_DateTimeOffset_type_handler()
    {
        _ = new AuditStoreEngine(new NotSupportedAuditDialect());

        Assert.False(SqlMapper.HasTypeHandler(typeof(DateTimeOffset)));
    }

    private sealed class NotSupportedAuditDialect : IAuditDialect
    {
        public DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();

        public string InsertSql => throw new NotSupportedException();

        public string SelectPageSql => throw new NotSupportedException();

        public string CountSql => throw new NotSupportedException();

        public string PurgeSql => throw new NotSupportedException();
    }
}
