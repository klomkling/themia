using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentPageValidatorTests
{
    private static readonly ContentOptions Options = CreateOptions();

    private static ContentOptions CreateOptions()
    {
        var options = new ContentOptions { FallbackLanguage = "th" };
        options.Languages.Add("th");
        options.Languages.Add("en");
        return options;
    }

    private static ContentPageSave ValidSave() =>
        new("privacy-policy", "en", "Privacy", "# Privacy", IsPublished: true, ExpectedVersion: 0, ChangeSummary: null, EditorId: "user-1");

    private static string[] FieldsOf(IReadOnlyList<ContentValidationError> errors) =>
        errors.Select(e => e.Field).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Validate_ShouldReturnNoError_WhenSaveIsValid() =>
        Assert.Empty(ContentPageValidator.Validate(ValidSave(), Options));

    [Theory]
    [InlineData("")]
    [InlineData("Privacy")]
    [InlineData("privacy_policy")]
    [InlineData("-privacy")]
    [InlineData("privacy--policy")]
    public void Validate_ShouldReportSlug_WhenSlugIsNotLowercaseWithSingleHyphens(string slug) =>
        Assert.Equal(["slug"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Slug = slug }, Options)));

    [Fact]
    public void Validate_ShouldReportSlug_WhenSlugIsLongerThan100() =>
        Assert.Equal(["slug"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Slug = new string('a', 101) }, Options)));

    [Fact]
    public void Validate_ShouldAcceptLanguage_WhenItNormalisesToAConfiguredOne() =>
        Assert.Empty(ContentPageValidator.Validate(ValidSave() with { Language = " EN " }, Options));

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    public void Validate_ShouldReportLanguage_WhenItIsNotConfigured(string language) =>
        Assert.Equal(["language"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Language = language }, Options)));

    [Fact]
    public void Validate_ShouldReportTitle_WhenBlankOrTooLong()
    {
        Assert.Equal(["title"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Title = " " }, Options)));
        Assert.Equal(["title"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Title = new string('t', 201) }, Options)));
    }

    [Fact]
    public void Validate_ShouldReportMarkdown_WhenBlank() =>
        Assert.Equal(["markdown"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Markdown = "" }, Options)));

    [Fact]
    public void Validate_ShouldReportEachMarkdownViolation_OnTheMarkdownField()
    {
        var errors = ContentPageValidator.Validate(
            ValidSave() with { Markdown = "a <b>x</b> [y](javascript:alert(1))" }, Options);

        Assert.Equal(["markdown"], FieldsOf(errors));
        Assert.Contains(errors, e => e.Message.Contains("raw HTML", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("javascript:alert(1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldReportExpectedVersion_WhenNegative() =>
        Assert.Equal(["expectedVersion"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { ExpectedVersion = -1 }, Options)));

    [Fact]
    public void Validate_ShouldReportChangeSummaryAndEditorId_WhenTooLong() =>
        Assert.Equal(
            ["changeSummary", "editorId"],
            FieldsOf(ContentPageValidator.Validate(
                ValidSave() with { ChangeSummary = new string('c', 501), EditorId = new string('e', 257) }, Options)));

    [Fact]
    public void Validate_ShouldTreatNullStringsAsMissing_NotThrow()
    {
        var errors = ContentPageValidator.Validate(new ContentPageSave(null!, null!, null!, null!, true, 0, null, null), Options);
        Assert.Equal(["language", "markdown", "slug", "title"], FieldsOf(errors));
    }

    [Fact]
    public void ValidateRevert_ShouldRequireATargetAndAnExistingVersion()
    {
        var errors = ContentPageValidator.Validate(new ContentPageRevert("terms", "th", 0, 0, null, null), Options);
        Assert.Equal(["expectedVersion", "targetVersion"], FieldsOf(errors));
    }

    [Fact]
    public void ValidateRevert_ShouldReturnNoError_WhenValid() =>
        Assert.Empty(ContentPageValidator.Validate(new ContentPageRevert("terms", "th", 1, 2, "undo", "user-1"), Options));

    [Fact]
    public void Result_ShouldSucceedOnlyWhenSaved()
    {
        var page = new ContentPage("terms", "th", "T", "# T", 1, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        Assert.True(ContentSaveResult.Saved(page).Succeeded);
        Assert.False(ContentSaveResult.Conflict(2, "b", DateTimeOffset.UnixEpoch).Succeeded);
        Assert.False(ContentSaveResult.Invalid([new ContentValidationError("slug", "bad")]).Succeeded);
        Assert.False(ContentSaveResult.NotFound().Succeeded);
        Assert.Equal(2, ContentSaveResult.Conflict(2, "b", DateTimeOffset.UnixEpoch).CurrentVersion);
    }
}
