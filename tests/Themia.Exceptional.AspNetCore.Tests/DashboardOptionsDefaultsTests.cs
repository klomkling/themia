using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class DashboardOptionsDefaultsTests
{
    [Fact]
    public void Defaults_EnableActionsAndShowRequestContext_True()
    {
        var o = new ExceptionalDashboardOptions();
        Assert.True(o.EnableActions);
        Assert.True(o.ShowRequestContext);
    }
}
