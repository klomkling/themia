using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DashboardOptionsDefaultsTests
{
    [Fact]
    public void Defaults_ShowRequestContext_True()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.True(o.ShowRequestContext);
    }

    [Fact]
    public void Defaults_EnableActions_False_SoDeleteRoutesAreOptIn()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.False(o.EnableActions);
    }
}
