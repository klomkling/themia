using Microsoft.Extensions.DependencyInjection;
using Themia.AI.DependencyInjection;
using Themia.AI.Gemini.DependencyInjection;
using Xunit;

namespace Themia.AI.Tests;

/// <summary>Task 8's DI-wiring tests: the real default graph, registration idempotency, and layering.</summary>
public sealed class AddThemiaAiTests
{
    /// <summary>
    /// A defect in 0.19.0 passed its unit tests and failed on the real default graph. This wires
    /// <c>AddThemiaAiGemini</c> and <c>AddThemiaAi</c> exactly as a host would, with no fakes, and
    /// resolves in a scope with <c>ValidateScopes = true</c>.
    /// </summary>
    [Fact]
    public void Both_services_resolve_from_the_graph_AddThemiaAi_builds()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThemiaAiGemini(o => { o.ApiKey = "k"; o.CompletionModel = "m"; o.TranslationModel = "m"; });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAiCompletionClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITextTranslationService>());
    }

    // Not in the plan's verbatim test, but the same real graph also has to resolve the masker — nothing
    // else in this file exercises it, and Step 1 added it with the same TryAdd shape as the other two.
    [Fact]
    public void The_default_masker_also_resolves_from_the_graph_AddThemiaAi_builds()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThemiaAiGemini(o => { o.ApiKey = "k"; o.CompletionModel = "m"; o.TranslationModel = "m"; });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IAiTextMasker>());
    }

    /// <summary>
    /// Every registration in <c>AddThemiaAi</c> is <c>TryAdd*</c>-based specifically so a second module
    /// that also depends on it (or a host that calls it more than once) does not fail startup or produce
    /// duplicate registrations. An Audit-round defect came from getting an equivalent registration wrong
    /// in the opposite direction — a hard <c>Add</c> that clobbered a caller's own service — so this is
    /// asserted directly rather than left to be noticed later.
    /// </summary>
    [Fact]
    public void Calling_AddThemiaAi_twice_does_not_throw_or_duplicate_registrations()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThemiaAiGemini(o => { o.ApiKey = "k"; o.CompletionModel = "m"; o.TranslationModel = "m"; });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IAiCompletionClient>());
        Assert.NotNull(sp.GetRequiredService<ITextTranslationService>());
        Assert.NotNull(sp.GetRequiredService<IAiTextMasker>());
    }

    /// <summary>
    /// A caller's own <see cref="IAiTextMasker"/>, registered <em>before</em> <c>AddThemiaAi</c>, must
    /// still win over the default — <c>TryAddSingleton</c> is a no-op once a registration already
    /// exists, regardless of which side registered first.
    /// </summary>
    [Fact]
    public void A_callers_own_masker_registered_first_wins_over_the_default()
    {
        var services = new ServiceCollection().AddLogging();
        var custom = new RecordingMasker();
        services.AddSingleton<IAiTextMasker>(custom);

        services.AddThemiaAiGemini(o => { o.ApiKey = "k"; o.CompletionModel = "m"; o.TranslationModel = "m"; });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);

        using var sp = services.BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<IAiTextMasker>());
    }

    private sealed class RecordingMasker : IAiTextMasker
    {
        public MaskedText Mask(string text, IEnumerable<string> valuesToMask)
            => new(text, new Dictionary<string, string>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Design and the plan's Global Constraints require <c>Themia.AI</c> to reference no
/// <c>Themia.Framework.*</c> package, no database, and no ASP.NET — it must stay usable by any .NET 8/10
/// host, not only a Themia one.
/// </summary>
/// <remarks>
/// Proving this test can fail needs a used type, not just a reference: Roslyn only emits an
/// <c>AssemblyRef</c> for an assembly whose types are actually referenced by IL, so a bare
/// <c>ProjectReference</c> with nothing calling into it is invisible to
/// <see cref="System.Reflection.Assembly.GetReferencedAssemblies"/> and would leave this test
/// permanently green regardless of what it asserts. See this task's report for how that was falsified.
/// </remarks>
public sealed class LayeringTests
{
    [Fact]
    public void Themia_AI_references_no_framework_database_or_aspnetcore_package()
    {
        var offenders = typeof(AiPrompt).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name =>
                name.StartsWith("Themia.Framework.", StringComparison.Ordinal) ||
                name.Contains("AspNetCore", StringComparison.Ordinal) ||
                name.Contains("EntityFrameworkCore", StringComparison.Ordinal) ||
                name.Contains("Dapper", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.Data.", StringComparison.Ordinal) ||
                name.StartsWith("Npgsql", StringComparison.Ordinal) ||
                name.StartsWith("MySql", StringComparison.Ordinal) ||
                name.StartsWith("System.Data.SqlClient", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }
}
