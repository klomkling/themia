using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Themia.Challenges;
using Themia.Challenges.DependencyInjection;
using Themia.Challenges.PostgreSql;
using Themia.SecondFactor.DependencyInjection;
using Themia.SecondFactor.PostgreSql;

namespace Themia.SecondFactor.IntegrationTests;

/// <summary>The second-factor service built the way an adopter builds it: real stores, real TOTP and Challenges, adopter fakes only for transport and secrets.</summary>
internal sealed class ServiceHarness : IAsyncDisposable
{
    public const string Issuer = "Themia Test";

    private readonly ServiceProvider root;

    public ServiceHarness(string connectionString, Action<SecondFactorOptions>? configure = null)
    {
        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Contacts = new FakeContacts();
        Email = new RecordingDelivery(MfaMethodKind.EmailOtp);
        Sms = new RecordingDelivery(MfaMethodKind.SmsOtp);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<ISecondFactorSecretProtector, Base64Protector>();
        services.AddSingleton<ISecondFactorContactResolver>(Contacts);
        services.AddSingleton<IOtpDelivery>(Email);
        services.AddSingleton<IOtpDelivery>(Sms);
        services.AddThemiaChallenges(options =>
        {
            options.PerKeyWindow = (1_000, TimeSpan.FromHours(1));
            options.ConfigurePurpose(SecondFactorOptions.EmailOtpPurpose, p => p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15)));
            options.ConfigurePurpose(SecondFactorOptions.SmsOtpPurpose, p => p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15)));
        });
        services.AddThemiaChallengesPostgres(connectionString);
        services.AddThemiaSecondFactorPostgres(connectionString);
        services.AddThemiaSecondFactor(
            RandomNumberGenerator.GetBytes(32),
            options =>
            {
                options.Issuer = Issuer;
                options.Policy.Default = MfaRule.Mandatory();
                configure?.Invoke(options);
            },
            webAuthn =>
            {
                webAuthn.ServerDomain = "localhost";
                webAuthn.ServerName = "Themia Test";
                webAuthn.Origins.Add("https://localhost");
            });
        root = services.BuildServiceProvider();
    }

    public FakeTimeProvider Clock { get; }

    public FakeContacts Contacts { get; }

    public RecordingDelivery Email { get; }

    public RecordingDelivery Sms { get; }

    public IServiceScope CreateScope() => root.CreateScope();

    public T Singleton<T>() where T : notnull => root.GetRequiredService<T>();

    public ValueTask DisposeAsync() => root.DisposeAsync();

    public static MfaSubject NewSubject(string? tenantId = null) =>
        new(Guid.NewGuid().ToString("D"), tenantId, new HashSet<string> { "staff" });

    public sealed class FakeContacts : ISecondFactorContactResolver
    {
        private readonly Dictionary<string, MfaContact> contacts = [];

        public void Set(MfaSubject subject, MfaContact contact) => contacts[subject.UserId] = contact;

        public Task<MfaContact?> ResolveAsync(MfaSubject subject, CancellationToken cancellationToken = default) =>
            Task.FromResult(contacts.GetValueOrDefault(subject.UserId));
    }

    public sealed class RecordingDelivery(MfaMethodKind kind) : IOtpDelivery
    {
        public MfaMethodKind Kind => kind;

        public bool Fail { get; set; }

        public List<(string Destination, string Code)> Sent { get; } = [];

        public Task SendAsync(string destination, string code, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("transport down");
            }

            Sent.Add((destination, code));
            return Task.CompletedTask;
        }
    }

    private sealed class Base64Protector : ISecondFactorSecretProtector
    {
        public string Scheme => "test-v1";

        public string Protect(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

        public string? Unprotect(string scheme, string protectedSecret) =>
            scheme == Scheme ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret)) : null;
    }
}
