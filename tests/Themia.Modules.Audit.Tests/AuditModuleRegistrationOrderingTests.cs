using Microsoft.Extensions.DependencyInjection;
using Themia.Audit;
using Themia.Audit.DependencyInjection;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Modules.Audit.DependencyInjection;
using Xunit;

namespace Themia.Modules.Audit.Tests;

// AddThemiaAuditModule must supersede AddThemiaAudit's neutral IAuditRecorder registration by identity,
// never by which call happened to run last (design §7). Resolving the neutral recorder instead would mean
// AuditTransactionPolicy.RequireTransaction is never enforced and Activity rows are written outside the
// caller's transaction, with no error to say so.
//
// These tests assert order-INDEPENDENCE rather than enforcing an order. AddThemiaAudit registers its
// recorder with TryAddSingleton, which no-ops when one already exists, and this module uses Replace — so
// both orders end at TransactionalAuditRecorder. That is a claim about two framework methods interacting,
// which is exactly the kind of thing that reads as obvious and is worth pinning: Add here instead of
// Replace, or a plain AddSingleton over there, and one of these two orders starts returning the wrong
// recorder silently.
// Modelled on tests/Themia.Modules.Messaging.Tests/DependencyInjection/MessagingRegistrationOrderingTests.cs.
public class AuditModuleRegistrationOrderingTests
{
    [Fact]
    public void Module_last_resolves_the_transactional_recorder_not_the_neutral_one()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Asserting only that IAuditRecorder resolves would pass even with the neutral, non-transactional
        // AuditRecorder still in place — the concrete type is the thing that must be right.
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        Assert.IsType<TransactionalAuditRecorder>(recorder);
    }

    // The order an adopter is least likely to write, and the one that would break first if either
    // registration switched to a plain Add/AddSingleton.
    [Fact]
    public void Module_first_also_resolves_the_transactional_recorder()
    {
        var services = new ServiceCollection();
        AddProviderFakes(services);
        services.AddThemiaAuditModule();
        AddNeutralAudit(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var recorders = scope.ServiceProvider.GetServices<IAuditRecorder>().ToList();
        Assert.Single(recorders);
        Assert.IsType<TransactionalAuditRecorder>(recorders[0]);
    }

    [Fact]
    public void Exactly_one_IAuditRecorder_registration_exists_after_module_last()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Single(scope.ServiceProvider.GetServices<IAuditRecorder>());
    }

    [Fact]
    public void Calling_AddThemiaAuditModule_twice_still_leaves_exactly_one_IAuditRecorder_registration()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();
        services.AddThemiaAuditModule();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var recorders = scope.ServiceProvider.GetServices<IAuditRecorder>().ToList();
        Assert.Single(recorders);
        Assert.IsType<TransactionalAuditRecorder>(recorders[0]);
    }

    [Fact]
    public void Correct_order_resolves_from_the_real_graph_under_scope_validation()
    {
        var services = BuildWithFakes();
        services.AddThemiaAuditModule();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        Assert.IsType<TransactionalAuditRecorder>(recorder);
    }

    private static ServiceCollection BuildWithFakes()
    {
        var services = new ServiceCollection();
        AddProviderFakes(services);
        AddNeutralAudit(services);
        return services;
    }

    // A provider package (e.g. Themia.Audit.PostgreSql) would normally supply these.
    private static void AddProviderFakes(IServiceCollection services)
    {
        services.AddSingleton<IAuditStore>(new FakeAuditStore());
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddScoped<IAmbientConnectionAccessor>(
            _ => new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() });
    }

    private static void AddNeutralAudit(IServiceCollection services) =>
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);
}
