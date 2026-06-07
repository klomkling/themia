using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Themia.Modules.Scheduling;

/// <summary>
/// Design-time factory enabling <c>dotnet ef migrations</c> for <see cref="SchedulingDbContext"/>.
/// </summary>
/// <remarks>
/// Used only by EF Core tooling at design time; never at runtime. The connection string is a
/// placeholder — migrations are generated from the model, not a live database.
/// </remarks>
public sealed class SchedulingDbContextFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    /// <inheritdoc />
    public SchedulingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql("Host=localhost;Database=themia_scheduling_design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new SchedulingDbContext(options);
    }
}
