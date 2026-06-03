using Themia.Framework.Core.Primitives;
using Xunit;

namespace Themia.Framework.Core.Tests.Primitives;

public class ValueObjectTests
{
    private sealed class Money : ValueObject
    {
        public Money(decimal amount, string currency)
        {
            Amount = amount;
            Currency = currency;
        }

        public decimal Amount { get; }

        public string Currency { get; }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    [Fact]
    public void Equals_ReturnsTrue_ForMatchingComponents()
    {
        var left = new Money(10m, "USD");
        var right = new Money(10m, "USD");

        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_ReturnsFalse_WhenComponentsDiffer()
    {
        var left = new Money(10m, "USD");
        var right = new Money(12m, "USD");

        Assert.True(left != right);
    }
}
