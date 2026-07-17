using Themia.Modules.Storage;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class StorageModuleOptionsTests
{
    [Fact]
    public void Validate_throws_when_PublicCacheMaxAge_is_negative()
    {
        var options = new StorageModuleOptions { PublicCacheMaxAge = TimeSpan.FromSeconds(-1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(StorageModuleOptions.PublicCacheMaxAge), ex.ParamName);
    }

    [Fact]
    public void Validate_accepts_a_zero_PublicCacheMaxAge()
    {
        var options = new StorageModuleOptions { PublicCacheMaxAge = TimeSpan.Zero };

        options.Validate(); // does not throw
    }
}
