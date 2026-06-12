using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Minimal <see cref="IDatabaseProvider"/> for the scheduling tests: the module only reads
/// <see cref="ProviderName"/> (to pick the EF provider + MigrationEngine); it configures the
/// DbContext itself, so Configure/ConfigureServices are intentionally no-ops.
/// </summary>
internal sealed class FakeDatabaseProvider(string providerName) : IDatabaseProvider
{
    public string ProviderName { get; } = providerName;

    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        // Not used by SchedulingModule (it configures its own DbContext).
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Not used by SchedulingModule.
    }
}
