using System.Data;
using Themia.Framework.Data.Dapper.Mapping;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

/// <summary>
/// Unit tests for the single-engine-per-process guard in <see cref="DapperConfiguration.ConfigureEngine"/>.
/// Dapper's type-handler registry is process-global, so registering two engines that bind
/// <see cref="DateTimeOffset"/> with different <see cref="DbType"/>s would silently corrupt one engine's
/// timestamp writes; the guard fails loudly instead. The <c>null</c> <see cref="DbType"/> here exercises the
/// slot-claim logic without mutating the global handler registry.
/// </summary>
public sealed class DapperEngineRegistrationTests
{
    [Fact]
    public void ConfigureEngine_SameEngineTwice_IsIdempotent()
    {
        DapperConfiguration.ResetEngineRegistrationForTests();
        try
        {
            DapperConfiguration.ConfigureEngine("PostgreSQL", dateTimeOffsetDbType: null);
            DapperConfiguration.ConfigureEngine("PostgreSQL", dateTimeOffsetDbType: null);   // no throw
        }
        finally
        {
            DapperConfiguration.ResetEngineRegistrationForTests();
        }
    }

    [Fact]
    public void ConfigureEngine_DifferentEngine_Throws()
    {
        DapperConfiguration.ResetEngineRegistrationForTests();
        try
        {
            DapperConfiguration.ConfigureEngine("MySQL", dateTimeOffsetDbType: null);

            var ex = Assert.Throws<InvalidOperationException>(
                () => DapperConfiguration.ConfigureEngine("SQL Server", dateTimeOffsetDbType: null));
            Assert.Contains("MySQL", ex.Message);
            Assert.Contains("SQL Server", ex.Message);
        }
        finally
        {
            DapperConfiguration.ResetEngineRegistrationForTests();
        }
    }
}
