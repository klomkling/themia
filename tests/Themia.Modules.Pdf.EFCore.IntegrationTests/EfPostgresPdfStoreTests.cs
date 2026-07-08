using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Abstractions.Exceptions;   // ISqlExceptionInterpreter, SqlStateUniqueConstraintInterpreter
using Themia.Framework.Data.EFCore.Abstractions;        // IDatabaseProvider
using Themia.Framework.Data.EFCore.PostgreSql;          // PostgresDatabaseProvider
using Themia.Modules.Pdf.IntegrationTests;
using Themia.Modules.Pdf.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Pdf.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfPostgresPdfStoreTests(PdfPostgresFixture fixture)
    : PdfStoreConformanceTests, IClassFixture<PdfPostgresFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        // AddThemiaPdfModuleEfCore builds its own DbContextFactory<PdfDbContext> and reads IDatabaseProvider
        // to pick UseNpgsql — so register the provider first. EfPdfTemplateStore news up an EfUnitOfWork that
        // needs ISqlExceptionInterpreter (normally registered by AddThemiaDataRepositories); the module does
        // not re-register it, so provide the framework default (SQLSTATE-based, PostgreSQL/MySQL) here.
        services.AddSingleton<IDatabaseProvider>(new PostgresDatabaseProvider());
        services.AddSingleton<ISqlExceptionInterpreter, SqlStateUniqueConstraintInterpreter>();
        services.AddThemiaPdfModuleEfCore();
    }
}
