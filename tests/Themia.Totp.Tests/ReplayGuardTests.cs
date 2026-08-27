using Microsoft.Extensions.Options;
using Themia.Totp;
using Xunit;

namespace Themia.Totp.Tests;

/// <summary>
/// The guard this package exists for. A TOTP code stays valid for its whole step, so an implementation
/// that only asks "does this match the window" is self-consistently correct and still lets an observer
/// replay the code for the rest of that window — and every test written from the RFC's description
/// passes without the guard.
/// </summary>
public sealed class ReplayGuardTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string SecretId = "user-1";

    /// <summary>
    /// The monotonic store the contract asks for: one highest-accepted step per credential, and a step
    /// at or below it is refused. The test owns this; the package ships no default (see
    /// AddThemiaTotpTests).
    /// </summary>
    private sealed class RecordingReplayStore : ITotpReplayStore
    {
        private readonly Dictionary<string, long> _highest = [];

        public List<(string SecretId, long Step)> Calls { get; } = [];

        public ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default)
        {
            Calls.Add((secretId, matchedStep));

            if (_highest.TryGetValue(secretId, out var highest) && matchedStep <= highest)
            {
                return ValueTask.FromResult(false);
            }

            _highest[secretId] = matchedStep;
            return ValueTask.FromResult(true);
        }
    }

    private static (TotpService Service, TestClock Clock, RecordingReplayStore Store) Build(
        int windowSteps = 1)
    {
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new RecordingReplayStore();
        var service = new TotpService(
            store, clock, Options.Create(new TotpOptions { VerificationWindowSteps = windowSteps }));
        return (service, clock, store);
    }

    [Fact]
    public async Task A_correct_code_is_accepted_once_and_reported_as_replayed_after()
    {
        var (service, _, _) = Build();
        var code = service.GenerateCode(Secret);

        var first = await service.VerifyAsync(SecretId, Secret, code);
        var second = await service.VerifyAsync(SecretId, Secret, code);

        Assert.Equal(TotpOutcome.Valid, first.Outcome);
        Assert.True(first.Succeeded);

        // Not InvalidCode: the code was genuinely issued, and a caller that cannot tell the difference
        // cannot alert on a replay attempt.
        Assert.Equal(TotpOutcome.Replayed, second.Outcome);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task The_step_that_MATCHED_is_consumed_not_the_step_the_clock_is_on()
    {
        // The subtle one. With a +/-1 tolerance a code minted for step S is accepted while the clock
        // still reads S-1. If the guard records the CURRENT step, the same code sails through again
        // once the clock reaches S - the guard passes its own test without closing the window.
        var (service, clock, store) = Build();

        var currentStep = clock.GetUtcNow().ToUnixTimeSeconds() / 30;
        clock.AdvanceSeconds(30);
        var nextStepCode = service.GenerateCode(Secret);   // minted for step S = currentStep + 1
        clock.Set(DateTimeOffset.FromUnixTimeSeconds(currentStep * 30));  // back to S-1

        var accepted = await service.VerifyAsync(SecretId, Secret, nextStepCode);
        Assert.Equal(TotpOutcome.Valid, accepted.Outcome);
        Assert.Equal(currentStep + 1, accepted.MatchedStep);

        // The store must have been told S, not S-1.
        Assert.Equal(currentStep + 1, Assert.Single(store.Calls).Step);

        // Now the clock catches up to S, where the same code is still inside the window.
        clock.Set(DateTimeOffset.FromUnixTimeSeconds((currentStep + 1) * 30));

        var replayed = await service.VerifyAsync(SecretId, Secret, nextStepCode);
        Assert.Equal(TotpOutcome.Replayed, replayed.Outcome);
    }

    [Fact]
    public async Task An_older_code_still_inside_the_window_is_refused_after_a_newer_one_is_used()
    {
        // The near-miss the monotonic contract exists for. Consuming only the matched step stops the
        // same code twice and still admits THIS: an observer captures the code for step S, the real
        // user signs in at S+1, and the captured code is then presented at S+1 — where a ±1 window
        // still covers step S, and nothing ever consumed it.
        var (service, clock, _) = Build(windowSteps: 1);

        var capturedAtS = service.GenerateCode(Secret);
        clock.AdvanceSeconds(30);
        var freshAtSPlus1 = service.GenerateCode(Secret);

        Assert.Equal(TotpOutcome.Valid, (await service.VerifyAsync(SecretId, Secret, freshAtSPlus1)).Outcome);

        var replayed = await service.VerifyAsync(SecretId, Secret, capturedAtS);

        Assert.Equal(TotpOutcome.Replayed, replayed.Outcome);
        Assert.False(replayed.Succeeded);
    }

    [Fact]
    public async Task A_wrong_code_is_invalid_and_never_reaches_the_store()
    {
        var (service, _, store) = Build();

        var result = await service.VerifyAsync(SecretId, Secret, "000000");

        Assert.Equal(TotpOutcome.InvalidCode, result.Outcome);
        Assert.Equal(-1, result.MatchedStep);
        // Consuming a step for a code that never matched would burn a step an honest user still needs.
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_code_from_an_adjacent_step_is_accepted_within_the_window(int stepOffset)
    {
        var (service, clock, _) = Build(windowSteps: 1);

        var start = clock.GetUtcNow();
        clock.Set(start.AddSeconds(30 * stepOffset));
        var code = service.GenerateCode(Secret);
        clock.Set(start);

        var result = await service.VerifyAsync(SecretId, Secret, code);

        Assert.Equal(TotpOutcome.Valid, result.Outcome);
    }

    [Fact]
    public async Task A_zero_window_accepts_only_the_current_step()
    {
        var (service, clock, _) = Build(windowSteps: 0);

        clock.AdvanceSeconds(30);
        var nextStepCode = service.GenerateCode(Secret);
        clock.AdvanceSeconds(-30);

        Assert.Equal(TotpOutcome.InvalidCode, (await service.VerifyAsync(SecretId, Secret, nextStepCode)).Outcome);
    }

    [Fact]
    public async Task Two_different_credentials_do_not_share_a_consumed_step()
    {
        var (service, _, _) = Build();
        var code = service.GenerateCode(Secret);

        Assert.Equal(TotpOutcome.Valid, (await service.VerifyAsync("user-1", Secret, code)).Outcome);

        // Same secret material in this test, but a different credential id: one user consuming a step
        // must not lock another out.
        Assert.Equal(TotpOutcome.Valid, (await service.VerifyAsync("user-2", Secret, code)).Outcome);
    }
}
