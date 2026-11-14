using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class AlertDigestBuilderTests
{
    private static readonly DateTime Seen = new(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc);

    private static ExceptionAlertOptions Options() => new()
    {
        Enabled = true,
        BotToken = "123456:secret-token-value",
        ChatId = "-1001",
        EnvironmentName = "Production",
        AdminUrlTemplate = "https://admin.example.test/exceptions/{guid}",
    };

    private static ExceptionAlertCandidate Candidate(int n, string? type = null, string? message = null) => new()
    {
        ExceptionId = n,
        Guid = Guid.NewGuid(),
        ApplicationName = "App",
        ErrorHash = "h" + n,
        Trigger = ExceptionAlertTrigger.New,
        Type = type ?? "System.InvalidOperationException",
#pragma warning disable CS0618 // Message is obsolete; set so the tests prove the digest never renders it
        Message = message ?? "boom",
#pragma warning restore CS0618
        DuplicateCount = 3,
        FirstSeenUtc = Seen,
        LastSeenUtc = Seen.AddMinutes(2),
    };

    private static string Words(string word) => string.Join(' ', Enumerable.Repeat(word, 250));

    [Fact]
    public void Build_ContainsAppEnvironmentTypeCountDatesAndLink()
    {
        var c = Candidate(1);
        var digest = AlertDigestBuilder.Build([c], "App", Options());

        Assert.Equal(1, digest.IncludedCount);
        Assert.Contains("App", digest.Text);
        Assert.Contains("Production", digest.Text);
        Assert.Contains("System.InvalidOperationException", digest.Text);
        Assert.DoesNotContain("boom", digest.Text);
        Assert.Contains("3", digest.Text);
        Assert.Contains("2026-11-10 05:00", digest.Text);
        Assert.Contains($"https://admin.example.test/exceptions/{c.Guid}", digest.Text);
    }

    [Fact]
    public void Build_NeverIncludesTheExceptionMessage()
    {
        var c = Candidate(1, message: "user a@b.example failed, token FAKE-SECRET-0123");
        var text = AlertDigestBuilder.Build([c], "App", Options()).Text;

        Assert.DoesNotContain("a@b.example", text);
        Assert.DoesNotContain("failed", text);
        Assert.DoesNotContain("FAKE-SECRET-0123", text);
    }

    [Fact]
    public void Build_MasksPersonalDataInTypeAndCollapsesNewlines()
    {
        var c = Candidate(1, type: "My.Ex a@b.example\nFAKE LINE phone 081 234 5678");
        var text = AlertDigestBuilder.Build([c], "App", Options()).Text;

        Assert.DoesNotContain("a@b.example", text);
        Assert.DoesNotContain("081 234 5678", text);
        Assert.DoesNotContain("example\nFAKE", text);
    }

    [Fact]
    public void Build_TenLongPoisonedEntries_StaysUnderTelegramLimitAndReportsHowManyFit()
    {
        var group = Enumerable.Range(1, 10)
            .Select(n => Candidate(n, Words("Type"), Words("msg"))).ToList();
        var options = Options();
        options.AdminUrlTemplate = "https://admin.example.test/" + new string('p', 300) + "/{guid}";
        var digest = AlertDigestBuilder.Build(group, "App", options);

        Assert.True(digest.Text.Length <= AlertDigestBuilder.MaxMessageLength, $"length {digest.Text.Length}");
        Assert.InRange(digest.IncludedCount, 1, 9);
        Assert.Contains($"+{10 - digest.IncludedCount} more", digest.Text);
    }

    [Fact]
    public void Build_NeverIncludesFieldsThatAreNotOnTheCandidate()
    {
        var text = AlertDigestBuilder.Build([Candidate(1)], "App", Options()).Text;
        Assert.DoesNotContain(Options().BotToken, text);
    }
}
