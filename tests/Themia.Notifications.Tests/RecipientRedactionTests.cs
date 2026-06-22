using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class RecipientRedactionTests
{
    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData("abcd", "****")]          // exactly 4 → fully masked (no leak)
    [InlineData("abcde", "*bcde")]        // 5 → keeps last 4
    [InlineData("user@example.com", "************.com")]
    public void Mask_RedactsAllButLastFour(string? input, string expected)
        => Assert.Equal(expected, RecipientRedaction.Mask(input));
}
