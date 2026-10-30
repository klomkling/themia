using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Themia.Challenges;
using Themia.Conversations.DependencyInjection;
using Themia.Conversations.Internal;
using Xunit;

namespace Themia.Conversations.Tests;

public sealed class GuestTests
{
    [Fact]
    public void SessionKey_ShouldNotShowItsValue_InToStringJsonOrTheActorsPrintout()
    {
        var key = GuestSessionKey.Generate();
        var actor = ConversationActor.ForGuest(key);

        Assert.DoesNotContain(key.Value, key.ToString());
        Assert.DoesNotContain(key.Value, actor.ToString());
        Assert.DoesNotContain(key.Value, JsonSerializer.Serialize(actor));
        Assert.DoesNotContain(key.Value, JsonSerializer.Serialize(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 44 characters
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")] // 43 characters, non-canonical last character
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+A")] // standard base64 character
    public void SessionKey_ShouldRefuseAMalformedValue(string value)
    {
        Assert.Throws<ArgumentException>(() => new GuestSessionKey(value));
        Assert.False(GuestSessionKey.TryParse(value, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void SessionKey_ShouldRoundTripAGeneratedKey_AndHashItDeterministically()
    {
        var key = GuestSessionKey.Generate();

        Assert.True(GuestSessionKey.TryParse(key.Value, out var parsed));
        Assert.Equal(key, parsed);
        Assert.Equal(key.Hash(), parsed!.Hash());
        Assert.Equal(32, key.Hash().Length);
        Assert.NotEqual(key, GuestSessionKey.Generate());
    }

    [Theory]
    [InlineData(GuestContactKind.Email, " Guest@Example.COM ", "email:guest@example.com")]
    [InlineData(GuestContactKind.Phone, "+66812345678", "phone:+66812345678")]
    [InlineData(GuestContactKind.Line, "U4af4980629AbC", "line:U4af4980629AbC")]
    public void Contact_ShouldNormaliseByKind_KeepingLineCase(GuestContactKind kind, string value, string expected)
    {
        Assert.True(GuestContactHasher.TryNormalise(new GuestContact(kind, value), out var input));
        Assert.Equal(expected, input);
    }

    [Theory]
    [InlineData(GuestContactKind.Phone, "0812345678")] // national form: the same person as +66812345678 would become a second guest
    [InlineData(GuestContactKind.Phone, "+66 81 234 5678")]
    [InlineData(GuestContactKind.Phone, "+0812345678")]
    [InlineData(GuestContactKind.Phone, "+1234567")]
    [InlineData(GuestContactKind.Email, "no-at-sign")]
    [InlineData(GuestContactKind.Email, "two@@example.com")]
    [InlineData(GuestContactKind.Email, "a b@example.com")]
    [InlineData(GuestContactKind.Line, "has space")]
    [InlineData(GuestContactKind.Line, "")]
    public void Contact_ShouldRefuseAMalformedValue(GuestContactKind kind, string value) =>
        Assert.False(GuestContactHasher.TryNormalise(new GuestContact(kind, value), out _));

    [Fact]
    public void Contact_ShouldHashDifferently_ForTheSameTextOfAnotherKind_AndForAnotherPepper()
    {
        var pepper = Enumerable.Repeat((byte)1, 32).ToArray();
        GuestContactHasher.TryNormalise(new GuestContact(GuestContactKind.Line, "+66812345678"), out var line);
        GuestContactHasher.TryNormalise(new GuestContact(GuestContactKind.Phone, "+66812345678"), out var phone);

        Assert.NotEqual(GuestContactHasher.Hash(pepper, line), GuestContactHasher.Hash(pepper, phone));
        Assert.NotEqual(GuestContactHasher.Hash(pepper, phone), GuestContactHasher.Hash(Enumerable.Repeat((byte)2, 32).ToArray(), phone));
        Assert.Equal(32, GuestContactHasher.Hash(pepper, phone).Length);
    }

    [Theory]
    [InlineData("mail a.b+c@mail.example.co.th now", "mail [email hidden] now")]
    [InlineData("call +66 81-234-5678", "call [phone hidden]")]
    [InlineData("โทร ๐๘๑๒๓๔๕๖๗๘", "โทร [phone hidden]")]
    [InlineData("line id: abc_01 ok", "[handle hidden] ok")]
    [InlineData("ping @somchai please", "ping [handle hidden] please")]
    [InlineData("room 12 at 5 pm", "room 12 at 5 pm")]
    public void Masker_ShouldReplaceContactDetails_ForAGuestViewer(string body, string expected)
    {
        var masked = new RegexConversationMasker().Mask(ConversationActorKind.Guest, Message(body));

        Assert.Equal(expected, masked.Body);
    }

    [Fact]
    public void Masker_ShouldLeaveTheTextWhole_ForAMemberViewer_AndRedactedBodiesAlone()
    {
        var masker = new RegexConversationMasker();

        Assert.Equal("a@b.co 0812345678", masker.Mask(ConversationActorKind.Member, Message("a@b.co 0812345678")).Body);
        Assert.Null(masker.Mask(ConversationActorKind.Guest, Message(null)).Body);
    }

    [Fact]
    public void Masker_ShouldFinishQuickly_OnAHostileLongInput()
    {
        var body = new string('1', 3000) + new string(' ', 500) + "@" + new string('a', 3000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            new RegexConversationMasker().Mask(ConversationActorKind.Guest, Message(body));
        }
        catch (RegexMatchTimeoutException)
        {
            // a timeout is the fail-closed path: the store hides the body
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void GuestOptions_ShouldFail_WithoutAPepper_AShortOne_OrAnUnknownCurrentId()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaConversationGuests(_ => { }));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaConversationGuests(o =>
        {
            o.Peppers["k1"] = new byte[31];
            o.CurrentPepperId = "k1";
        }));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaConversationGuests(o =>
        {
            o.Peppers["k1"] = new byte[32];
            o.CurrentPepperId = "k2";
        }));
    }

    [Fact]
    public void GuestOptions_ShouldDefaultTheSessionToSevenDays()
    {
        var options = new ConversationGuestOptions();

        Assert.Equal(TimeSpan.FromDays(7), options.SessionLifetime);
    }

    [Fact]
    public async Task Startup_ShouldFail_WhenTheGuestPurposeIsMissingOrNotAnOpaqueToken()
    {
        var missing = new ChallengeOptions();
        var numeric = new ChallengeOptions();
        numeric.ConfigurePurpose(ConversationGuestOptions.ChallengePurpose, p => p.Format = ChallengeFormat.Numeric(6));
        var good = new ChallengeOptions();
        good.ConfigureConversationGuestPurpose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(missing).StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Validator(numeric).StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GuestConfigurationValidator(new ServiceCollection().BuildServiceProvider()).StartAsync(default));
        await Validator(good).StartAsync(default);
    }

    [Fact]
    public void GuestPurpose_ShouldBeAnOpaqueToken_WithOneLiveLink_AndSeventyTwoHours()
    {
        var options = new ChallengeOptions();
        options.ConfigureConversationGuestPurpose();

        var purpose = options.GetPurpose(ConversationGuestOptions.ChallengePurpose);
        Assert.Equal(ChallengeFormatKind.OpaqueToken, purpose.Format.Kind);
        Assert.Equal(32, purpose.Format.Length);
        Assert.Equal(1, purpose.MaxLiveChallenges);
        Assert.Equal(TimeSpan.FromHours(72), purpose.Ttl);
    }

    private static GuestConfigurationValidator Validator(ChallengeOptions options) =>
        new(new ServiceCollection().AddSingleton(options).BuildServiceProvider());

    private static ConversationMessage Message(string? body) =>
        new(Guid.NewGuid(), 1, Guid.NewGuid(), MessageKind.Text, body, DateTimeOffset.UtcNow, false);
}
