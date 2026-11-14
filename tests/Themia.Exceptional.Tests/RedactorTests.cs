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
    [InlineData("__Secure-themia_dash", "c")]
    [InlineData("ticket", "t")]
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

    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.c2lnbmF0dXJl";

    [Theory]
    [InlineData("jwt")]
    [InlineData("sid")]
    [InlineData("X-Forwarded-Auth")]
    [InlineData("next")]
    [InlineData("anything")]
    public void DefaultRedactor_MasksAJwtUnderAnyKey(string key)
        => Assert.Equal("***", ExceptionalOptions.DefaultRedactor(key, Jwt));

    private const string UnsignedJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJhZG1pbiJ9.";

    [Theory]
    [InlineData("jwt")]
    [InlineData("next")]
    [InlineData("anything")]
    public void DefaultRedactor_MasksAnUnsignedJwtWithAnEmptySignatureUnderAnyKey(string key)
        => Assert.Equal("***", ExceptionalOptions.DefaultRedactor(key, UnsignedJwt));

    [Fact]
    public void DefaultRedactor_MasksAnUnsignedJwtInsideLongerText()
        => Assert.Equal("redirect=/x?t=*** done", ExceptionalOptions.DefaultRedactor("note", $"redirect=/x?t={UnsignedJwt} done"));

    [Fact]
    public void DefaultRedactor_MasksAJwtInsideLongerText()
        => Assert.Equal("redirect=/x?t=*** done", ExceptionalOptions.DefaultRedactor("note", $"redirect=/x?t={Jwt} done"));

    [Theory]
    [InlineData("X-Custom", "Bearer abcdefgh12345", "***")]
    [InlineData("X-Custom", "Authorization: Digest username=a, nonce=b", "***")]
    [InlineData("note", "failed with password=hunter2 for user", "failed with *** for user")]
    public void DefaultRedactor_MasksCredentialShapesUnderUnlistedKeys(string key, string value, string expected)
        => Assert.Equal(expected, ExceptionalOptions.DefaultRedactor(key, value));

    [Theory]
    [InlineData("Client-Credential")]
    [InlineData("aws_credentials")]
    [InlineData("X-Credential")]
    [InlineData("db_passwd")]
    [InlineData("pwd")]
    [InlineData("access-key")]
    public void DefaultRedactor_MasksCredentialKeys(string key)
        => Assert.Equal("***", ExceptionalOptions.DefaultRedactor(key, "abc123def"));

    [Theory]
    [InlineData("id", "3f2b8c1e-4d5a-4b6c-8d7e-9f0a1b2c3d4e")]
    [InlineData("order", "202611100042")]
    [InlineData("hash", "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("when", "2026-11-10T09:30:00Z")]
    [InlineData("name", "Jane Doe")]
    [InlineData("mail", "jane@example.com")]
    public void DefaultRedactor_KeepsOrdinaryValuesReadable(string key, string value)
        => Assert.Equal(value, ExceptionalOptions.DefaultRedactor(key, value));
}
