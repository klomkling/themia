using Themia.Geo;
using Xunit;

namespace Themia.Geo.Tests;

public sealed class LayeringTests
{
    [Fact]
    public void Themia_Geo_references_no_framework_package()
    {
        var offenders = typeof(GeoPoint).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Themia.Framework.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }
}
