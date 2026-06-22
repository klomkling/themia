using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("Authorization", "Bearer abc")]
    [InlineData("Cookie", "session=xyz")]
    [InlineData("Set-Cookie", "session=xyz")]
    [InlineData("password", "hunter2")]
    [InlineData("ApiKey", "k-123")]
    [InlineData("api-key", "k-123")]
    [InlineData("api_key", "k-123")]
    [InlineData("X-Api-Key", "k-123")]
    [InlineData("X-Session-Token", "t-9")]
    [InlineData(".AspNetCore.Cookies", "c")]
    [InlineData(".AspNetCore.Identity.Application", "c")]
    public void DefaultRedactor_MasksSecretKeys(string key, string value)
        => Assert.Equal("***", ExceptionalOptions.DefaultRedactor(key, value));

    [Theory]
    [InlineData("User-Agent", "Edge")]
    [InlineData("email", "x@y.com")]
    [InlineData("id", "42")]
    [InlineData("author", "Jane")]
    [InlineData("monkey", "George")]
    public void DefaultRedactor_KeepsNonSecretValues(string key, string value)
        => Assert.Equal(value, ExceptionalOptions.DefaultRedactor(key, value));

    [Fact]
    public void Defaults_CaptureOffAndDefaultRedactor()
    {
        var o = new ExceptionalOptions();
        Assert.False(o.CaptureRequestContext);
        Assert.NotNull(o.Redactor);
        Assert.Equal("***", o.Redactor!("Authorization", "Bearer x"));
    }
}
