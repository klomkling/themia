using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Themia.Modules.Identity.IntegrationTests;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.Dapper.SqlServer.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DapperSqlServerIdentityTests(SqlServerIdentityFixture fixture)
    : IdentityStoreConformanceTests, IClassFixture<SqlServerIdentityFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();
    protected override Task<IReadOnlyList<string>> ReadAllTextAsync() => fixture.ReadAllTextAsync();

    protected override void RegisterIdentity(IServiceCollection services, Action<IdentityModuleOptions> configure)
        => services.AddThemiaIdentityDapper(configure);

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaDapperSqlServer(configuration);
    }

    [Fact] // coord #0233: a data layer that shows global rows to a tenant scope must not let a swap write one
    public async Task Link_replacing_holder_refuses_a_global_row_when_global_records_are_shown_to_tenants()
    {
        await ResetAsync();
        Guid platformHolder;
        var identity = new ExternalIdentity("telegram", "tg-global-visible", null, false, null);
        await using (var platform = NewScope(tenant: null))
        {
            platformHolder = (await platform.Users.CreateAsync("global-holder", "pw")).UserId!.Value;
            Assert.Equal(ExternalLoginLinkOutcome.Linked, (await platform.Linking.LinkAsync(platformHolder, identity)).Outcome);
        }

        await using var tenantScope = NewScope(
            new TenantId("acme"),
            configureServices: services =>
            {
                var options = (DapperDataOptions)services.Single(d => d.ServiceType == typeof(DapperDataOptions)).ImplementationInstance!;
                options.IncludeGlobalRecordsForTenants = true;
            });
        var claimant = (await tenantScope.Users.CreateAsync("tenant-claimant", "pw")).UserId!.Value;

        var result = await tenantScope.Linking.LinkReplacingHolderAsync(claimant, identity, platformHolder);

        Assert.Equal(ExternalLoginLinkOutcome.LinkedToAnotherUser, result.Outcome);
        Assert.Empty(await tenantScope.Linking.GetLoginsAsync(claimant));
        await using var check = NewScope(tenant: null);
        Assert.Single(await check.Linking.GetLoginsAsync(platformHolder));
    }
}
