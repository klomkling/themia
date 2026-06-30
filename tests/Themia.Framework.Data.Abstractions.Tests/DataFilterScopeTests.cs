using Themia.Framework.Data.Abstractions.Filtering;
using Xunit;

namespace Themia.Framework.Data.Abstractions.Tests;

public sealed class DataFilterScopeTests
{
    [Fact]
    public void BypassSoftDeleteFilter_is_scoped_and_independent_of_tenant_bypass()
    {
        var scope = new DataFilterScope();
        Assert.False(scope.IsSoftDeleteFilterBypassed);
        Assert.False(DataFilterScope.SoftDeleteBypassedAmbient);

        using (scope.BypassSoftDeleteFilter())
        {
            Assert.True(scope.IsSoftDeleteFilterBypassed);
            Assert.True(DataFilterScope.SoftDeleteBypassedAmbient);
            Assert.False(scope.IsTenantFilterBypassed); // axes are independent
        }

        Assert.False(scope.IsSoftDeleteFilterBypassed);
        Assert.False(DataFilterScope.SoftDeleteBypassedAmbient);
    }
}
