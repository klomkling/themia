using Themia.Exceptional.Conformance;

using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

[Collection(PostgresExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class PostgresExceptionStoreTests(PostgresExceptionalFixture fixture) : ExceptionStoreConformanceTests
{
    private ExceptionStoreEngine Engine => new(new PostgresExceptionalDialect(fixture.ConnectionString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    protected override IExceptionalSqlDialect Dialect => new PostgresExceptionalDialect(fixture.ConnectionString);

    protected override Task ResetStoreAsync() => fixture.ResetAsync();
}
