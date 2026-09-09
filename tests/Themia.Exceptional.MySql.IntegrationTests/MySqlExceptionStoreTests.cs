using Themia.Exceptional.Conformance;

using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

[Collection(MySqlExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class MySqlExceptionStoreTests(MySqlExceptionalFixture fixture) : ExceptionStoreConformanceTests
{
    // No GuidFormat suffix: MySqlExceptionalDialect applies GuidFormat=Char36 itself, so a plain
    // connection string round-trips System.Guid ↔ CHAR(36) — this exercises that behavior.
    private ExceptionStoreEngine Engine => new(new MySqlExceptionalDialect(fixture.ConnectionString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    protected override Task ResetStoreAsync() => fixture.ResetAsync();
}
