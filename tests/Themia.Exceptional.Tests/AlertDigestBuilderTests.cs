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
        Message = message ?? "boom",
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
        Assert.Contains("boom", digest.Text);
        Assert.Contains("3", digest.Text);
        Assert.Contains("2026-11-10 05:00", digest.Text);
        Assert.Contains($"https://admin.example.test/exceptions/{c.Guid}", digest.Text);
    }

    [Fact]
    public void Build_MasksPersonalDataInMessageAndCollapsesNewlines()
    {
        var c = Candidate(1, message: "user a@b.example failed\nFAKE LINE phone 081 234 5678");
        var text = AlertDigestBuilder.Build([c], "App", Options()).Text;

        Assert.DoesNotContain("a@b.example", text);
        Assert.DoesNotContain("081 234 5678", text);
        Assert.DoesNotContain("failed\nFAKE", text);
    }

    [Fact]
    public void Build_TenLongPoisonedEntries_StaysUnderTelegramLimitAndReportsHowManyFit()
    {
        var group = Enumerable.Range(1, 10)
            .Select(n => Candidate(n, Words("Type"), Words("msg"))).ToList();
        var digest = AlertDigestBuilder.Build(group, "App", Options());

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
