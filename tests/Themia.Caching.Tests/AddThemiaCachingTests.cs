using Microsoft.Extensions.DependencyInjection;
using Themia.Caching;
using Themia.Caching.Extensions;
using Xunit;

namespace Themia.Caching.Tests;

public class AddThemiaCachingTests
{
    [Fact]
    public void AddThemiaCaching_MemoryProvider_ResolvesCacheProvider()
    {
        var services = new ServiceCollection();
        services.AddThemiaCaching();                 // default registration (memory provider)
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetService<IThemiaCacheProvider>();
        Assert.NotNull(provider);
    }
}
