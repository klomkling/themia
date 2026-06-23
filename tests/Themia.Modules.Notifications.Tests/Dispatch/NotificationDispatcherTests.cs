using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Dispatch;

public class NotificationDispatcherTests
{
    private sealed class FakePreferenceResolver(IReadOnlyList<NotificationChannel> enabled) : IPreferenceResolver
    {
        public Task<ResolvedPreferences> ResolveAsync(
            string userId, IReadOnlyList<NotificationChannel> requested, CancellationToken ct = default)
            => Task.FromResult(new ResolvedPreferences(enabled, Locale: null));
    }

    private sealed class RecordingOutboxStore : IOutboxStore
    {
        public List<OutboxMessage> Enqueued { get; } = [];

        public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
        {
            Enqueued.Add(message);
            return Task.CompletedTask;
        }
    }

    // Recording fake: AddAsync captures the staged entity but never "saves".
    private sealed class RecordingInAppRepository : IRepository<InAppNotification, Guid>
    {
        public List<InAppNotification> Staged { get; } = [];
        public int SaveCalls { get; private set; }

        public Task AddAsync(InAppNotification entity, CancellationToken cancellationToken = default)
        {
            Staged.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(InAppNotification entity) => SaveCalls++;
        public void Remove(InAppNotification entity) => SaveCalls++;

        public Task<int> UpdateWhereAsync(
            ISpecification<InAppNotification> specification,
            Action<IBulkUpdateSetters<InAppNotification>> set,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InAppNotification?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<InAppNotification>> ListAsync(
            ISpecification<InAppNotification> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<InAppNotification?> FirstOrDefaultAsync(
            ISpecification<InAppNotification> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> CountAsync(
            ISpecification<InAppNotification> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> AnyAsync(
            ISpecification<InAppNotification> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PagedResult<InAppNotification>> PageAsync(
            ISpecification<InAppNotification> specification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingRenderer : INotificationTemplateRenderer
    {
        public int Calls { get; private set; }

        public string Render(string template, object model)
        {
            Calls++;
            return $"rendered:{template}";
        }
    }

    private static NotificationDispatcher Build(
        IReadOnlyList<NotificationChannel> enabled,
        RecordingOutboxStore outbox,
        RecordingInAppRepository inApp,
        RecordingRenderer renderer,
        DateTimeOffset now)
        => new(
            new FakePreferenceResolver(enabled),
            outbox,
            inApp,
            renderer,
            new TenantContext(new TenantId("acme")),
            new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Dispatch_email_and_sms_enqueues_two_rendered_pending_rows()
    {
        var now = DateTimeOffset.UnixEpoch;
        var outbox = new RecordingOutboxStore();
        var inApp = new RecordingInAppRepository();
        var renderer = new RecordingRenderer();
        var dispatcher = Build(
            [NotificationChannel.Email, NotificationChannel.Sms], outbox, inApp, renderer, now);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.Email, NotificationChannel.Sms],
            Recipients = new Dictionary<NotificationChannel, string>
            {
                [NotificationChannel.Email] = "to@example.com",
                [NotificationChannel.Sms] = "+15551234567",
            },
            Subject = "hi",
            Template = "tmpl",
            Model = new { name = "x" },
        });

        Assert.Equal(2, outbox.Enqueued.Count);
        Assert.Empty(inApp.Staged);
        Assert.Equal(1, renderer.Calls); // rendered once, reused across channels
        Assert.All(outbox.Enqueued, m =>
        {
            Assert.Equal(OutboxStatus.Pending, m.Status);
            Assert.Equal("rendered:tmpl", m.Body);
            Assert.Equal(now, m.NextAttemptAt);
            Assert.Equal(new TenantId("acme"), m.TenantId);
        });
    }

    [Fact]
    public async Task Dispatch_in_app_stages_on_repository_without_saving()
    {
        var now = DateTimeOffset.UnixEpoch;
        var outbox = new RecordingOutboxStore();
        var inApp = new RecordingInAppRepository();
        var renderer = new RecordingRenderer();
        var dispatcher = Build([NotificationChannel.InApp], outbox, inApp, renderer, now);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.InApp],
            Subject = "Welcome",
            Body = "hello",
        });

        Assert.Empty(outbox.Enqueued);
        var staged = Assert.Single(inApp.Staged);
        Assert.Equal("Welcome", staged.Title);
        Assert.Equal("hello", staged.Body);
        Assert.Equal("user-1", staged.UserId);
        Assert.Equal(now, staged.CreatedAt);
        Assert.NotEqual(Guid.Empty, staged.Id);
        Assert.Equal(0, inApp.SaveCalls); // staging only — never saved
    }

    [Fact]
    public async Task Dispatch_with_verbatim_body_does_not_call_renderer()
    {
        var outbox = new RecordingOutboxStore();
        var inApp = new RecordingInAppRepository();
        var renderer = new RecordingRenderer();
        var dispatcher = Build([NotificationChannel.Email], outbox, inApp, renderer, DateTimeOffset.UnixEpoch);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.Email],
            Recipients = new Dictionary<NotificationChannel, string> { [NotificationChannel.Email] = "to@example.com" },
            Body = "verbatim",
        });

        Assert.Equal(0, renderer.Calls);
        var message = Assert.Single(outbox.Enqueued);
        Assert.Equal("verbatim", message.Body);
    }

    [Fact]
    public async Task Dispatch_skips_channel_disabled_by_preference()
    {
        var outbox = new RecordingOutboxStore();
        var inApp = new RecordingInAppRepository();
        var renderer = new RecordingRenderer();
        // Requested Email+Sms, but preferences enable only Email.
        var dispatcher = Build([NotificationChannel.Email], outbox, inApp, renderer, DateTimeOffset.UnixEpoch);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.Email, NotificationChannel.Sms],
            Recipients = new Dictionary<NotificationChannel, string>
            {
                [NotificationChannel.Email] = "to@example.com",
                [NotificationChannel.Sms] = "+15551234567",
            },
            Body = "x",
        });

        var message = Assert.Single(outbox.Enqueued);
        Assert.Equal(NotificationChannel.Email, message.Channel);
    }

    [Fact]
    public async Task Dispatch_throws_when_no_channels_requested()
    {
        var dispatcher = Build(
            [NotificationChannel.Email], new RecordingOutboxStore(), new RecordingInAppRepository(),
            new RecordingRenderer(), DateTimeOffset.UnixEpoch);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [],
            Body = "x",
        }));
        Assert.Equal("request", ex.ParamName);
    }

    [Fact]
    public async Task Dispatch_with_scheduled_for_sets_next_attempt_and_scheduled_for()
    {
        var now = DateTimeOffset.UnixEpoch;
        var scheduledFor = now.AddHours(6);
        var outbox = new RecordingOutboxStore();
        var dispatcher = Build(
            [NotificationChannel.Email], outbox, new RecordingInAppRepository(),
            new RecordingRenderer(), now);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.Email],
            Recipients = new Dictionary<NotificationChannel, string> { [NotificationChannel.Email] = "to@example.com" },
            Body = "x",
            ScheduledFor = scheduledFor,
        });

        var message = Assert.Single(outbox.Enqueued);
        Assert.Equal(scheduledFor, message.NextAttemptAt);
        Assert.Equal(scheduledFor, message.ScheduledFor);
    }

    [Fact]
    public async Task Dispatch_skips_external_channel_with_missing_recipient()
    {
        var outbox = new RecordingOutboxStore();
        var inApp = new RecordingInAppRepository();
        var renderer = new RecordingRenderer();
        var dispatcher = Build([NotificationChannel.Email], outbox, inApp, renderer, DateTimeOffset.UnixEpoch);

        await dispatcher.DispatchAsync(new NotificationRequest
        {
            UserId = "user-1",
            Channels = [NotificationChannel.Email],
            Recipients = null, // no address for Email
            Body = "x",
        });

        Assert.Empty(outbox.Enqueued);
    }
}
