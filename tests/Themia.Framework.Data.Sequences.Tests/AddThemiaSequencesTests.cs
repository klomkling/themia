using Microsoft.Extensions.DependencyInjection;

using Themia.Framework.Data.Sequences;

using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class AddThemiaSequencesTests
{
    [Fact]
    public void AddThemiaSequences_RegistersTheProvider()
    {
        var services = new ServiceCollection();

        services.AddThemiaSequences(o =>
        {
            o.ConnectionString = "Host=localhost;Database=x;Username=u;Password=p";
            o.Engine = SequenceEngine.Postgres;
        });

        Assert.Contains(services, d => d.ServiceType == typeof(ISequenceProvider));
    }

    [Fact]
    public void AddThemiaSequences_ValidatesEagerly_SoAMisconfigurationFailsAtStartup()
    {
        // Not at the first allocation: a connection string typo should stop the deploy, not surface as a
        // failed invoice hours later.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaSequences(o => o.Engine = SequenceEngine.Postgres));

        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaSequences_RejectsANullConfigureCallback()
        => Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddThemiaSequences(null!));

    [Fact]
    public void AddThemiaSequences_Twice_WithDifferentOptions_Throws()
    {
        // TryAddSingleton silently discards the second registration. A host registering the main database
        // and a module registering its own would send EVERY allocation to whichever ran first, with no
        // diagnostic anywhere -- and the loser's Validate() would have passed, so it looks configured.
        var services = new ServiceCollection();
        services.AddThemiaSequences(o =>
        {
            o.ConnectionString = "Host=primary;Database=x;Username=u;Password=p";
            o.Engine = SequenceEngine.Postgres;
        });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaSequences(o =>
        {
            o.ConnectionString = "Host=secondary;Database=y;Username=u;Password=p";
            o.Engine = SequenceEngine.Postgres;
        }));

        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The message must not leak the connection strings -- they carry credentials.
        Assert.DoesNotContain("Password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("primary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaSequences_Twice_WithIdenticalOptions_IsIdempotent()
    {
        // Registering the same configuration twice is harmless and should stay harmless -- only a
        // CONFLICTING second registration is the bug.
        var services = new ServiceCollection();
        void Configure(SequenceOptions o)
        {
            o.ConnectionString = "Host=primary;Database=x;Username=u;Password=p";
            o.Engine = SequenceEngine.Postgres;
        }

        services.AddThemiaSequences(Configure);
        services.AddThemiaSequences(Configure);

        Assert.Single(services, d => d.ServiceType == typeof(SequenceOptions));
    }
}
