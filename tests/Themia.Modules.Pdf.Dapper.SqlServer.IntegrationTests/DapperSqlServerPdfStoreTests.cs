using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.DependencyInjection;            // AddThemiaDapperCore
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;  // AddThemiaDapperSqlServer
using Themia.Modules.Pdf.IntegrationTests;
using Themia.Modules.Pdf.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Pdf.Dapper.SqlServer.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DapperSqlServerPdfStoreTests(PdfSqlServerFixture fixture)
    : PdfStoreConformanceTests, IClassFixture<PdfSqlServerFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        // Deliberately DO NOT set IncludeGlobalRecordsForTenants — it stays false (default), proving the
        // Dapper store's For<PdfTemplate>(includeGlobalRecords: true) override drives the global fallback.
        services.AddThemiaDapperCore();
        services.AddThemiaDapperSqlServer(configuration);
        services.AddThemiaPdfModuleDapper();
    }
}
