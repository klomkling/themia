using Xunit;

namespace Themia.Content.Tests;

public class ContentMarkdownRulesTests
{
    [Theory]
    [InlineData("## Cookies {#cookies}")]
    [InlineData("## คุกกี้ {#cookies}")]
    [InlineData("[x](https://a.example/p?q=1#f)")]
    [InlineData("[x](/privacy#cookies)")]
    [InlineData("[x](#cookies)")]
    [InlineData("[x](/a:b)")]
    [InlineData("<mailto:privacy@a.example>")]
    [InlineData("<a@b.com>")]
    [InlineData("[call](tel:+6621234567)")]
    [InlineData("```\n<b>x</b>\n```")]
    [InlineData("```\n<b>x</b>")]
    [InlineData("x ``a`<b>`` y")]
    [InlineData("use `<br>` here")]
    [InlineData("para\n\n    <b>x</b>\n")]
    [InlineData("> ```\n> <b>x</b>\n> ```")]
    public void Check_ShouldReturnNoViolation_WhenMarkdownIsSafe(string markdown) =>
        Assert.Empty(ContentMarkdownRules.Check(markdown));

    [Theory]
    [InlineData("a <script>x</script> b")]
    [InlineData("a <!-- hidden --> b")]
    [InlineData("<div>\nhello\n</div>")]
    [InlineData("`\n\n<b>x</b>\n\n`")]
    [InlineData("[<b>x</b>](https://a.example)")]
    public void Check_ShouldReportRawHtml_WhenMarkdownContainsHtmlOutsideCode(string markdown)
    {
        var violations = ContentMarkdownRules.Check(markdown);
        Assert.Contains(violations, v => v.Kind == ContentMarkdownViolationKind.RawHtml);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JAVASCRIPT:alert(1))")]
    [InlineData("[x](javascript&#58;alert(1))")]
    [InlineData("[x](javascript&colon;alert(1))")]
    [InlineData("[x](<javascript:alert(1)>)")]
    [InlineData("[x][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("![x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("```\ncode\n````\n\n[x](javascript:alert(1))")]
    [InlineData("[x](java&#9;script:alert(1))")]
    [InlineData("[x](&#x6A&#x61vascript:alert(1))")]
    public void Check_ShouldReportDisallowedUrl_WhenADestinationHasAnUnsafeScheme(string markdown)
    {
        var violations = ContentMarkdownRules.Check(markdown);
        Assert.Contains(violations, v => v.Kind == ContentMarkdownViolationKind.DisallowedUrl);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/a:b", true)]
    [InlineData("#cookies", true)]
    [InlineData("?q=1", true)]
    [InlineData("HTTPS://a.example", true)]
    [InlineData("mailto:privacy@a.example", true)]
    [InlineData("tel:+6621234567", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("java&#9;script:alert(1)", false)]
    [InlineData("&#x6A&#x61vascript:alert(1)", false)]
    [InlineData("javascript&colon;alert(1)", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("java&#99999999;script:alert(1)", false)]
    public void IsUrlAllowed_ShouldAllowOnlyTheFourSchemesOrNone(string? url, bool expected) =>
        Assert.Equal(expected, ContentMarkdownRules.IsUrlAllowed(url));

    [Fact]
    public void Check_ShouldThrow_WhenMarkdownIsNull() =>
        Assert.Throws<ArgumentNullException>(() => ContentMarkdownRules.Check(null!));
}
