using Microsoft.Extensions.DependencyInjection;
using Themia.Audit;
using Themia.Audit.DependencyInjection;
using Themia.Framework.Data.Abstractions.Connections;
using Themia.Modules.Audit.DependencyInjection;
using Themia.Services.Abstractions;
using Xunit;

namespace Themia.Modules.Audit.Tests;

public class AuditLogServiceAdapterTests
{
    [Fact]
    public async Task Maps_AuditEvent_onto_AuditEntry_per_spec()
    {
        var recorder = new FakeAuditRecorder();
        var adapter = new AuditLogServiceAdapter(recorder);
        var occurredAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string> { ["k"] = "v" };

        await adapter.WriteAsync(new AuditEvent("PROPOSAL_ACCEPTED", "user-1", "Alice", "proposal-9", "Proposal", occurredAt, metadata));

        var entry = recorder.LastEntry;
        Assert.NotNull(entry);
        Assert.Equal(AuditCategory.Activity, entry!.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal("PROPOSAL_ACCEPTED", entry.EventType);
        Assert.Equal("user-1", entry.ActorId);
        Assert.Equal("Alice", entry.ActorName);
        Assert.Equal("proposal-9", entry.EntityId);
        Assert.Equal("Proposal", entry.EntityType);
        Assert.Equal(occurredAt, entry.OccurredAt);
        Assert.Same(metadata, recorder.LastPayload);
    }

    [Fact]
    public async Task IAuditLogService_resolves_from_the_real_DI_graph()
    {
        // Built by AddThemiaAudit + AddThemiaAuditModule, not a hand-assembled container: a defect that
        // resolves fine against hand-picked fakes but not the real DI graph is exactly what shipped
        // unnoticed in the 0.19.0 notification-provider seam.
        await using var sp = BuildRealHost().CreateAsyncScope();

        var svc = sp.ServiceProvider.GetRequiredService<IAuditLogService>();

        await svc.WriteAsync(new AuditEvent("EVT", "a", "n", "e", "T", DateTimeOffset.UtcNow), default);
    }

    private static ServiceProvider BuildRealHost()
    {
        var services = new ServiceCollection();

        // A provider package (e.g. Themia.Audit.PostgreSql) would normally supply these; substitute
        // fakes since this project references no provider package. The ambient connection accessor
        // simulates being inside a transaction, so the default Activity/RequireTransaction policy
        // succeeds without a real database.
        services.AddSingleton<IAuditStore>(new FakeAuditStore());
        services.AddSingleton<IAuditDialect, FakeAuditDialect>();
        services.AddScoped<IAmbientConnectionAccessor>(
            _ => new FakeAmbientConnectionAccessor { Ambient = FakeAmbientConnectionAccessor.NewAmbient() });

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = "fake";
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);

        services.AddThemiaAuditModule();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
