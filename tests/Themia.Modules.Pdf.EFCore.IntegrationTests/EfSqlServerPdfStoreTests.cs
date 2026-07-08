using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Abstractions.Exceptions;   // ISqlExceptionInterpreter, SqlStateUniqueConstraintInterpreter
using Themia.Framework.Data.EFCore.Abstractions;        // IDatabaseProvider
using Themia.Framework.Data.EFCore.SqlServer;           // SqlServerDatabaseProvider
using Themia.Modules.Pdf.IntegrationTests;
using Themia.Modules.Pdf.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Pdf.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfSqlServerPdfStoreTests(PdfSqlServerFixture fixture)
    : PdfStoreConformanceTests, IClassFixture<PdfSqlServerFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        // Same shape as EfPostgresPdfStoreTests, swapping the provider to SQL Server. AddThemiaPdfModuleEfCore
        // builds its own DbContextFactory<PdfDbContext> and reads IDatabaseProvider to pick UseSqlServer.
        // EfPdfTemplateStore news up an EfUnitOfWork that needs ISqlExceptionInterpreter; the module does not
        // register it, and the SQL Server number-based interpreter is internal, so provide the public framework
        // default. The duplicate-key fact asserts ThrowsAny, which the raw DbUpdateException already satisfies.
        services.AddSingleton<IDatabaseProvider>(new SqlServerDatabaseProvider());
        services.AddSingleton<ISqlExceptionInterpreter, SqlStateUniqueConstraintInterpreter>();
        services.AddThemiaPdfModuleEfCore();
    }
}
