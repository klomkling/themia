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
}
