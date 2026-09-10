using Themia.AI;
using Xunit;

namespace Themia.AI.Tests;

public class ContractTests
{
    [Fact]
    public void Unset_enums_are_not_valid_values()
    {
        Assert.False(Enum.IsDefined(default(AiOutcome)) && default(AiOutcome) != AiOutcome.Unspecified);
        Assert.Equal(AiOutcome.Unspecified, default(AiOutcome));
        Assert.Equal(AiOperation.Unspecified, default(AiOperation));
    }
}
