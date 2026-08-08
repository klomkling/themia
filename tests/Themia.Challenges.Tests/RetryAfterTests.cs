using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// <c>RateLimited</c> now carries how long until the window resets (coord #0064). ezy-assets' four OTP
/// flows answer 429 + <c>Retry-After</c> with a live countdown — a contract Themia built for them on
/// coord #0001, its very first request — and moving those flows onto <c>Themia.Challenges</c> would have
/// regressed it, because an adopter cannot compute the reset: the counter rows are ours and nothing on
/// <c>IChallengeService</c> exposes them.
/// <para>
/// Shipped as <b>data, never policy</b>. Nothing here turns the value into a status code or a header:
/// propertiezy's three anonymous endpoints answer uniformly on purpose, and an automatic mapping would
/// have flipped them from uniform to distinguishable on upgrade with no diff on their side.
/// </para>
/// </summary>
public sealed class RetryAfterTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection keepAlive;
    private readonly string connString;
    private readonly FakeTimeProvider time;

    public RetryAfterTests()
    {
        connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connString);
        keepAlive.Open();
        Dapper.SqlMapper.Execute(keepAlive, SqliteChallengeDialect.CreateTablesSql);
        time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
    }

    public void Dispose() => keepAlive.Dispose();

    private ChallengeService CreateService(Action<ChallengeOptions> configure)
    {
        var options = new ChallengeOptions();
        configure(options);
        return new ChallengeService(new SqliteChallengeDialect(connString), options, time, NullLogger<ChallengeService>.Instance);
    }

    private static void Numeric(PurposeOptions p, (int Limit, TimeSpan Window)? perScopeWindow = null)
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = TimeSpan.FromMinutes(5);
        p.PerScopeWindow = perScopeWindow ?? (100, TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Issue_RateLimited_carries_the_time_until_the_window_resets()
    {
        // Window starts at 00:00 and is 15 minutes long. Refused 4 minutes in => 11 minutes left.
        var service = CreateService(o => o.ConfigurePurpose("login", p => Numeric(p, (1, TimeSpan.FromMinutes(15)))));
        var scope = new ChallengeScope("+66111111111", "login", "tenantA");

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        time.Advance(TimeSpan.FromMinutes(4));

        var refused = await service.IssueAsync(scope);

        Assert.Equal(ChallengeIssueOutcome.RateLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(11), refused.RetryAfter);
    }

    [Fact]
    public async Task Issue_RetryAfter_is_null_when_the_call_succeeded()
    {
        var service = CreateService(o => o.ConfigurePurpose("login", p => Numeric(p)));

        var issued = await service.IssueAsync(new ChallengeScope("+66222222222", "login", "tenantA"));

        Assert.Equal(ChallengeIssueOutcome.Issued, issued.Outcome);
        Assert.Null(issued.RetryAfter);
    }

    [Fact]
    public async Task Issue_reports_the_LATEST_reset_when_two_layers_are_over()
    {
        // The per-scope window (15m) resets long before the per-key one (1h). Reporting the earlier reset
        // would send the caller back into a refusal it could have predicted — every configured layer has
        // to be under its ceiling before the next call succeeds.
        var service = CreateService(o =>
        {
            o.PerKeyWindow = (1, TimeSpan.FromHours(1));
            o.ConfigurePurpose("login", p => Numeric(p, (1, TimeSpan.FromMinutes(15))));
        });
        var scope = new ChallengeScope("+66333333333", "login", "tenantA");

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        var refused = await service.IssueAsync(scope);

        Assert.Equal(ChallengeIssueOutcome.RateLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromHours(1), refused.RetryAfter);
    }

    [Fact]
    public async Task Issue_reports_the_per_scope_reset_when_only_that_layer_is_over()
    {
        // The mirror of the test above: a generous per-key ceiling must not inflate the answer.
        var service = CreateService(o =>
        {
            o.PerKeyWindow = (1_000, TimeSpan.FromHours(1));
            o.ConfigurePurpose("login", p => Numeric(p, (1, TimeSpan.FromMinutes(15))));
        });
        var scope = new ChallengeScope("+66444444444", "login", "tenantA");

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(scope)).Outcome);
        var refused = await service.IssueAsync(scope);

        Assert.Equal(TimeSpan.FromMinutes(15), refused.RetryAfter);
    }

    [Fact]
    public async Task Issue_reports_the_global_key_reset_when_that_is_the_layer_over()
    {
        var service = CreateService(o =>
        {
            o.PerKeyWindow = (1_000, TimeSpan.FromHours(1));
            o.PerKeyGlobalWindow = (1, TimeSpan.FromHours(6));
            o.ConfigurePurpose("login", p => Numeric(p, (1_000, TimeSpan.FromMinutes(15))));
        });
        const string key = "+66555555555";

        Assert.Equal(ChallengeIssueOutcome.Issued, (await service.IssueAsync(new ChallengeScope(key, "login", "tenantA"))).Outcome);
        var refused = await service.IssueAsync(new ChallengeScope(key, "login", "tenantB"));

        Assert.Equal(ChallengeIssueOutcome.RateLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromHours(6), refused.RetryAfter);
    }

    [Fact]
    public async Task Verify_RateLimited_carries_the_time_until_the_verify_window_resets()
    {
        var service = CreateService(o =>
        {
            o.VerifyWindow = (1, TimeSpan.FromMinutes(15));
            o.ConfigurePurpose("login", p => Numeric(p));
        });
        var scope = new ChallengeScope("+66666666666", "login", "tenantA");

        await service.VerifyAsync(scope, "000000");
        time.Advance(TimeSpan.FromMinutes(5));

        var refused = await service.VerifyAsync(scope, "000000");

        Assert.Equal(ChallengeVerifyOutcome.RateLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(10), refused.RetryAfter);
    }

    [Fact]
    public async Task Verify_RetryAfter_is_null_on_every_other_outcome()
    {
        // Only a rate-limit refusal knows about a window. An Incorrect or NotFound result carrying a
        // value would be a second enumeration signal on the exact endpoint that must not have one.
        var service = CreateService(o =>
        {
            o.VerifyWindow = (100, TimeSpan.FromMinutes(15));
            o.ConfigurePurpose("login", p => Numeric(p));
        });
        var scope = new ChallengeScope("+66777777777", "login", "tenantA");

        Assert.Null((await service.VerifyAsync(scope, "000000")).RetryAfter);   // NotFound

        var issue = await service.IssueAsync(scope);
        Assert.Null((await service.VerifyAsync(scope, "999999")).RetryAfter);   // Incorrect
        Assert.Null((await service.VerifyAsync(scope, issue.Secret!)).RetryAfter);  // Verified
    }

    [Fact]
    public void A_negative_retry_after_is_refused_rather_than_handed_to_a_caller()
    {
        // A caller casting a negative TimeSpan into a Retry-After header emits a header no client obeys.
        Assert.Throws<ArgumentOutOfRangeException>(() => ChallengeIssueResult.RateLimited(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChallengeVerifyResult.RateLimited(new ChallengeScope("k", "login"), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void The_parameterless_factories_still_mean_not_determined()
    {
        // Kept, and kept meaning null: "could not work it out" must stay distinguishable from "retry now",
        // or a call site's `?? 0` silently becomes a hardcoded guess.
        Assert.Null(ChallengeIssueResult.RateLimited().RetryAfter);
        Assert.Null(ChallengeVerifyResult.RateLimited(new ChallengeScope("k", "login")).RetryAfter);
    }
}
