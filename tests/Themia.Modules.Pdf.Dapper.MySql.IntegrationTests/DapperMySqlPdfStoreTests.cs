using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.DependencyInjection;         // AddThemiaDapperCore
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;   // AddThemiaDapperMySql
using Themia.Modules.Pdf.IntegrationTests;
using Themia.Modules.Pdf.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Pdf.Dapper.MySql.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DapperMySqlPdfStoreTests(PdfMySqlFixture fixture)
    : PdfStoreConformanceTests, IClassFixture<PdfMySqlFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        // Deliberately DO NOT set IncludeGlobalRecordsForTenants — it stays false (default), proving the
        // Dapper store's For<PdfTemplate>(includeGlobalRecords: true) override drives the global fallback.
        services.AddThemiaDapperCore();
        services.AddThemiaDapperMySql(configuration);
        services.AddThemiaPdfModuleDapper();
    }
}
