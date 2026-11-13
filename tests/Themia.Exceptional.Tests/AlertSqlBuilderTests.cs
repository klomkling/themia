using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class AlertSqlBuilderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Selects_NeverReadTheExceptionMessageColumn(int flavorValue)
    {
        var sql = new AlertSqlBuilder((AlertSqlFlavor)flavorValue);

        Assert.DoesNotContain("Message", sql.Candidates);
        Assert.DoesNotContain("Message", sql.ClaimedRows);
    }
}
