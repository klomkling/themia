using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// The lock keys are a wire format between processes: every instance of an application must derive the
/// identical key from the identical database, or they contend on different locks and the lock silently
/// protects nothing. These are golden vectors — computed independently of the implementation — so a change
/// to the derivation shows up as a failing test rather than as a migration race in production.
/// </summary>
public class MigrationLockKeyTests
{
    private const string PropertiezyScope = "themia:data:migrations:propertiezy";
    private const string EzyAssetsScope = "themia:data:migrations:ezy_assets";

    [Theory]
    [InlineData(PropertiezyScope, -4968616187050878533L)]
    [InlineData(EzyAssetsScope, -1537333437132418426L)]
    public void NumericKey_ShouldMatchTheGoldenVector(string scope, long expected)
    {
        Assert.Equal(expected, MigrationLock.NumericKey(scope));
    }

    [Theory]
    [InlineData(PropertiezyScope, "themia_migrate_bb999319e7ed0bbb")]
    [InlineData(EzyAssetsScope, "themia_migrate_86526452624baaea")]
    public void TextKey_ShouldMatchTheGoldenVector(string scope, string expected)
    {
        Assert.Equal(expected, MigrationLock.TextKey(scope));
    }

    [Fact]
    public void Keys_ShouldDiffer_BetweenDatabases()
    {
        // The reason the database name is in the key at all: PostgreSQL advisory locks and MySQL's GET_LOCK
        // are server-global, so two Themia applications sharing one server must not serialize against each
        // other's migrations.
        Assert.NotEqual(MigrationLock.NumericKey(PropertiezyScope), MigrationLock.NumericKey(EzyAssetsScope));
        Assert.NotEqual(MigrationLock.TextKey(PropertiezyScope), MigrationLock.TextKey(EzyAssetsScope));
    }

    [Fact]
    public void TextKey_ShouldFitMySqlLockNameLimit()
    {
        // MySQL rejects GET_LOCK names longer than 64 characters.
        Assert.True(MigrationLock.TextKey("themia:data:migrations:" + new string('d', 200)).Length <= 64);
    }
}
