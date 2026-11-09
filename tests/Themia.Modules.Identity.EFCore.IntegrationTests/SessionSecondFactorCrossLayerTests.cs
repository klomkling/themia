using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Dapper.DependencyInjection;
using Themia.Modules.Identity.EFCore.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.EFCore.IntegrationTests;

/// <summary>The EF Core and Dapper layers share one schema: a <c>session_second_factor</c> row written by one is read back whole by the
/// other, on each engine. The key column is <c>family_id</c>, which the Dapper convention only finds through an override.</summary>
public abstract class SessionSecondFactorCrossLayerTests
{
    private static readonly DateTimeOffset VerifiedAt = new(2026, 11, 8, 3, 4, 5, TimeSpan.Zero);

    protected abstract string ConnectionString { get; }
    protected abstract Task ResetAsync();
    protected abstract void ConfigureEf(IServiceCollection services, IConfiguration configuration);
    protected abstract void ConfigureDapper(IServiceCollection services, IConfiguration configuration);

    private sealed record Layer(ServiceProvider Provider, AsyncServiceScope Scope) : IAsyncDisposable
    {
        public IUserService Users => Scope.ServiceProvider.GetRequiredService<IUserService>();
        public IRepository<SessionSecondFactor, Guid> Rows => Scope.ServiceProvider.GetRequiredService<IRepository<SessionSecondFactor, Guid>>();
        public IUnitOfWork UnitOfWork => Scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private Layer Build(bool ef)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        if (ef)
        {
            ConfigureEf(services, configuration);
            services.AddThemiaIdentityEFCore(_ => { });
        }
        else
        {
            ConfigureDapper(services, configuration);
            services.AddThemiaIdentityDapper(_ => { });
        }

        var provider = services.BuildServiceProvider();
        return new Layer(provider, provider.CreateAsyncScope());
    }

    private static SessionSecondFactor Row(Guid sessionId, Guid userId) => new()
    {
        Id = sessionId,
        UserId = userId,
        Amr = "otp mfa",
        Strength = SessionProofStrength.Strong,
        AuthenticatedAt = VerifiedAt,
        Restriction = SessionRestriction.RecoveryOnly,
        CreatedAt = VerifiedAt,
        UpdatedAt = VerifiedAt,
    };

    [Fact]
    public async Task A_row_the_ef_layer_wrote_reads_back_whole_through_dapper()
    {
        await ResetAsync();
        var sessionId = Guid.NewGuid();
        Guid userId;
        await using (var ef = Build(ef: true))
        {
            userId = (await ef.Users.CreateAsync("ida", "pw")).UserId!.Value;
            await ef.Rows.AddAsync(Row(sessionId, userId));
            await ef.UnitOfWork.SaveChangesAsync();
        }

        await using var dapper = Build(ef: false);
        AssertWhole(await dapper.Rows.GetByIdAsync(sessionId), sessionId, userId);
    }

    [Fact]
    public async Task A_row_the_dapper_layer_wrote_reads_back_whole_through_ef()
    {
        await ResetAsync();
        var sessionId = Guid.NewGuid();
        Guid userId;
        await using (var dapper = Build(ef: false))
        {
            userId = (await dapper.Users.CreateAsync("ida", "pw")).UserId!.Value;
            await dapper.Rows.AddAsync(Row(sessionId, userId));
            await dapper.UnitOfWork.SaveChangesAsync();
        }

        await using var ef = Build(ef: true);
        AssertWhole(await ef.Rows.GetByIdAsync(sessionId), sessionId, userId);
    }

    private static void AssertWhole(SessionSecondFactor? row, Guid sessionId, Guid userId)
    {
        Assert.NotNull(row);
        Assert.Equal(sessionId, row.Id);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("otp mfa", row.Amr);
        Assert.Equal(SessionProofStrength.Strong, row.Strength);
        Assert.Equal(VerifiedAt, row.AuthenticatedAt);
        Assert.Equal(SessionRestriction.RecoveryOnly, row.Restriction);
    }
}
