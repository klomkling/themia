using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class AlertMessageMaskerTests
{
    [Theory]
    [InlineData("user somchai@example.com not found", "user *** not found")]
    [InlineData("call 081-234-5678 now", "call *** now")]
    [InlineData("id 1 2345 67890 12 3 end", "id *** end")]
    [InlineData("card 4111 1111 1111 1111 declined", "card *** declined")]
    [InlineData("bad jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc_DEF-123 here", "bad jwt *** here")]
    [InlineData("key abcdefghijklmnopqrstuvwxyz0123456789 used", "key *** used")]
    public void Mask_HidesPersonalDataAndTokens(string input, string expected)
        => Assert.Equal(expected, AlertMessageMasker.Mask(input));

    [Theory]
    [InlineData("login failed password=hunter2 for user", "login failed *** for user")]
    [InlineData("connection string has Pwd: s3cr3t!", "connection string has ***")]
    [InlineData("body {\"password\":\"hunter2\",\"user\":1}", "body {***,\"user\":1}")]
    [InlineData("body {\"api_key\": \"two words\"}", "body {***}")]
    [InlineData("cfg 'token' = 'a b c' end", "cfg *** end")]
    [InlineData("login password=a,b;c} next", "login *** next")]
    [InlineData("body {\"password\":\"pa\\\"ss word\",\"u\":1}", "body {***,\"u\":1}")]
    [InlineData("x token: 'it\\'s secret' y", "x *** y")]
    [InlineData("password=\"unclosed secret more text", "***")]
    [InlineData("auth: 'never closed, still secret", "***")]
    [InlineData("grant access_token=abc123 failed", "grant *** failed")]
    [InlineData("refresh_token: abc123 failed", "*** failed")]
    [InlineData("{\"id_token\":\"abc\",\"u\":1}", "{***,\"u\":1}")]
    [InlineData("client_secret=abc123 bad", "*** bad")]
    [InlineData("DB_PASSWORD=abc123 bad", "*** bad")]
    [InlineData("X-Api-Key: abc123 bad", "*** bad")]
    [InlineData("service_credential=abc123 bad", "*** bad")]
    [InlineData("MY_TOKEN=abc123 bad", "*** bad")]
    [InlineData("Authorization: Digest username=\"a\", realm=\"b\", nonce=\"c\"\nnext line", "***\nnext line")]
    [InlineData("Authorization: Negotiate YIIabc tail", "***")]
    [InlineData("{\"authorization\":\"line1\nline2 secret\",\"u\":1}", "{***,\"u\":1}")]
    [InlineData("authorization: 'never closed\nsecond line", "***")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz tail", "***")]
    [InlineData("api_key=abc123 rejected", "*** rejected")]
    [InlineData("header Authorization: Bearer abc.def-ghi_jkl123 refused", "header ***")]
    [InlineData("blob QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5Kw== bad", "blob *** bad")]
    public void Mask_HidesKeyValueSecretsBearerValuesAndBase64(string input, string expected)
        => Assert.Equal(expected, AlertMessageMasker.Mask(input));

    [Theory]
    [InlineData("Object reference not set to an instance of an object.")]
    [InlineData("Timeout after 30 seconds on order 12345")]
    public void Mask_LeavesOrdinaryMessagesUntouched(string input)
        => Assert.Equal(input, AlertMessageMasker.Mask(input));
}
