using System.Data;
using Themia.Framework.Data.Dapper.Mapping;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

/// <summary>
/// Serializes the tests that mutate the process-global engine slot so they can never run concurrently with
/// each other (or with any future test that calls <see cref="DapperConfiguration.ConfigureEngine"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DapperEngineRegistrationCollection
{
    public const string Name = "DapperEngineRegistration";
}

/// <summary>
/// Unit tests for the single-engine-per-process guard in <see cref="DapperConfiguration.ConfigureEngine"/>.
/// Dapper's type-handler registry is process-global, so registering two engines that bind
/// <see cref="DateTimeOffset"/> with different <see cref="DbType"/>s would silently corrupt one engine's
/// timestamp writes; the guard fails loudly instead. The <c>null</c> <see cref="DbType"/> here exercises the
/// slot-claim logic without mutating the global handler registry.
/// </summary>
[Collection(DapperEngineRegistrationCollection.Name)]
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

/// <summary>
/// Unit tests for the shared <see cref="DapperConfiguration.UtcDateTimeOffsetTypeHandler"/> read path used by the
/// MySQL and SQL Server engines: a tz-naive <see cref="DateTime"/> is read back as UTC, an existing
/// <see cref="DateTimeOffset"/> passes through, and an unexpected stored type fails loudly. (The write path and
/// microsecond precision are covered end-to-end by the engine integration suites.) These are pure — no global
/// state — so they need no collection guard.
/// </summary>
public sealed class UtcDateTimeOffsetTypeHandlerTests
{
    private static readonly DapperConfiguration.UtcDateTimeOffsetTypeHandler Handler = new(DbType.DateTime2);

    [Fact]
    public void Parse_TzNaiveDateTime_IsLabeledUtc()
    {
        var stored = new DateTime(2026, 6, 10, 9, 8, 7, DateTimeKind.Unspecified);

        var result = Handler.Parse(stored);

        Assert.Equal(TimeSpan.Zero, result.Offset);                 // re-labeled UTC
        Assert.Equal(stored, result.UtcDateTime);
    }

    [Fact]
    public void Parse_DateTimeOffset_PassesThrough()
    {
        var stored = new DateTimeOffset(2026, 6, 10, 9, 8, 7, TimeSpan.FromHours(7));

        var result = Handler.Parse(stored);

        Assert.Equal(stored, result);
    }

    [Fact]
    public void Parse_UnexpectedType_Throws()
    {
        Assert.Throws<InvalidCastException>(() => Handler.Parse("not a date"));
    }
}
