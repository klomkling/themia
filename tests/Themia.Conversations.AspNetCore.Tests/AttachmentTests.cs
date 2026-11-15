using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Themia.Conversations;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class AttachmentTests
{
    private static readonly Guid Room = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Member = "tenant-a:u1";

    private static async Task<(TestApp App, FakeConversationAttachments Files)> StartAsync(Action<ConversationOptions>? limits = null)
    {
        var files = new FakeConversationAttachments();
        var app = await TestApp.StartAsync(services: s =>
        {
            s.AddSingleton<IConversationAttachments>(files);
            if (limits is not null)
            {
                s.Configure(limits);
            }
        });
        return (app, files);
    }

    private static HttpRequestMessage Form(string clientMessageId, int files, int bytesEach = 3, string? extraField = null, string fileField = "files")
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(clientMessageId), "clientMessageId");
        content.Add(new StringContent("hello"), "body");
        for (var i = 0; i < files; i++)
        {
            var part = new ByteArrayContent(new byte[bytesEach]);
            part.Headers.ContentType = new("image/png");
            content.Add(part, fileField, $"../dir\\a{i}.png");
        }

        if (extraField is not null)
        {
            content.Add(new StringContent("x"), extraField);
        }

        return new HttpRequestMessage(HttpMethod.Post, TestApp.Path(Room, "messages")) { Content = content };
    }

    [Fact]
    public async Task Post_ShouldPassFilesToTheService_WhenMultipart()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(Form("c1", 2), member: Member);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var post = Assert.Single(files.Posts);
        Assert.Equal("c1", post.Message.ClientMessageId);
        Assert.Equal(2, post.Files.Count);
        Assert.Equal(("a0.png", "image/png", 3L), post.Files[0]);
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldReturn422WithoutCallingTheService_WhenThereAreTooManyFiles()
    {
        var (app, files) = await StartAsync(o => o.MaxAttachmentsPerMessage = 2);
        await using var _ = app;

        var response = await app.SendAsync(Form("c1", 3), member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("too_many_files", await response.Content.ReadAsStringAsync());
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn413WithoutCallingTheService_WhenTheBodyIsOverTheCap()
    {
        var (app, files) = await StartAsync(o => o.MaxAttachmentBytesPerMessage = 1024);
        await using var _ = app;

        var request = Form("c1", 1, bytesEach: 200_000);
        request.Content!.Headers.ContentLength = null;
        var response = await app.SendAsync(request, member: Member);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn422_WhenAFieldIsUnknown()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(Form("c1", 1, extraField: "admin"), member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn422_WhenAFilePartHasAnotherName()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(Form("c1", 1, fileField: "upload"), member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn422_WhenTheClientMessageIdIsInvalid()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(Form(" bad id ", 1), member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn403WithoutReadingTheBody_WhenMultipartLacksTheCsrfHeader()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(Form("c1", 1), csrf: false, member: Member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, app.BodyReads.Count);
        Assert.Empty(files.Posts);
    }

    [Fact]
    public async Task Post_ShouldReturn415_WhenAttachmentsAreNotRegistered()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(Form("c1", 1), member: Member);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Post_ShouldMapAttachmentOutcomes()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        files.PostResult = new(PostOutcome.AttachmentRejected, null, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.SendAsync(Form("c1", 1), member: Member)).StatusCode);
        files.PostResult = new(PostOutcome.QuotaExceeded, null, null);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await app.SendAsync(Form("c2", 1), member: Member)).StatusCode);
        files.PostResult = new(PostOutcome.Refused, null, null);
        Assert.Equal(HttpStatusCode.NotFound, (await app.SendAsync(Form("c3", 1), member: Member)).StatusCode);
    }

    [Fact]
    public async Task Open_ShouldReturnTheUrlWithNoStore_WhenTheServiceOpens()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(Room, "attachments/33333333-3333-3333-3333-333333333333"), member: Member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("https://files.example/signed?x=1", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, files.Opens);
    }

    [Fact]
    public async Task Open_ShouldReturn404_WhenTheServiceRefuses()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;
        files.OpenResult = new(OpenAttachmentOutcome.Refused, null, null);

        var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(Room, "attachments/33333333-3333-3333-3333-333333333333"), member: Member);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Open_ShouldReturn401WithoutCallingTheService_WhenThereIsNoCredential()
    {
        var (app, files) = await StartAsync();
        await using var _ = app;

        var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(Room, "attachments/33333333-3333-3333-3333-333333333333"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, files.Opens);
    }
}
