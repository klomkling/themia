using Xunit;

namespace Themia.Conversations.Tests;

public sealed class ConversationOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchTheDesign()
    {
        var o = new ConversationOptions();

        Assert.Equal(4000, o.MaxBodyLength);
        Assert.Equal(80, o.MaxDisplayNameLength);
        Assert.Equal(10, o.MaxAttachmentsPerMessage);
        Assert.Equal(10L * 1024 * 1024, o.MaxAttachmentBytesPerMessage);
        Assert.Equal(200L * 1024 * 1024, o.MaxAttachmentBytesPerConversation);
        Assert.Equal(100, o.MaxListLimit);
        Assert.Equal(TimeSpan.FromDays(180), o.DefaultRetention);
        var ex = Record.Exception(o.Validate);
        Assert.Null(ex);
    }

    [Fact]
    public void RetentionFor_ShouldUseThePerSubjectTypeValue_AndOtherwiseTheDefault()
    {
        var o = new ConversationOptions();
        o.RetentionBySubjectType["booking"] = TimeSpan.FromDays(30);

        Assert.Equal(TimeSpan.FromDays(30), o.RetentionFor("booking"));
        Assert.Equal(TimeSpan.FromDays(180), o.RetentionFor("job"));
        Assert.Equal(TimeSpan.FromDays(180), o.RetentionFor("Booking"));   // matched exactly, case-sensitive
    }

    public static TheoryData<string, Action<ConversationOptions>> Invalid() => new()
    {
        { nameof(ConversationOptions.MaxBodyLength), o => o.MaxBodyLength = 0 },
        { nameof(ConversationOptions.MaxBodyLength), o => o.MaxBodyLength = 4001 },
        { nameof(ConversationOptions.MaxDisplayNameLength), o => o.MaxDisplayNameLength = 81 },
        { nameof(ConversationOptions.MaxAttachmentsPerMessage), o => o.MaxAttachmentsPerMessage = 0 },
        { nameof(ConversationOptions.MaxAttachmentBytesPerMessage), o => o.MaxAttachmentBytesPerMessage = 0 },
        { nameof(ConversationOptions.MaxAttachmentBytesPerConversation), o => o.MaxAttachmentBytesPerConversation = o.MaxAttachmentBytesPerMessage - 1 },
        { nameof(ConversationOptions.MaxListLimit), o => o.MaxListLimit = 0 },
        { nameof(ConversationOptions.DefaultRetention), o => o.DefaultRetention = TimeSpan.Zero },
        { nameof(ConversationOptions.RetentionBySubjectType), o => o.RetentionBySubjectType["booking"] = TimeSpan.FromDays(-1) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Validate_ShouldRefuseAnOutOfRangeOption_NamingIt(string option, Action<ConversationOptions> change)
    {
        var o = new ConversationOptions();
        change(o);

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);

        Assert.Contains(option, ex.Message);
    }
}
