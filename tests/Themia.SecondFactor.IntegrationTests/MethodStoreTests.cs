using Microsoft.Extensions.DependencyInjection;

using Themia.SecondFactor;
using Themia.SecondFactor.PostgreSql;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MethodStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const int Parallelism = 24;

    private IMfaMethodStore Store => fixture.Get<IMfaMethodStore>();

    private static string NewUser() => Guid.NewGuid().ToString("D");

    private static NewMfaMethod Totp(string userId, string? tenant, long step = -1) => new()
    {
        TenantId = tenant, UserId = userId, Kind = MfaMethodKind.Totp, Label = "Authenticator",
        SecretProtected = "cipher", ProtectionScheme = "test-v1", LastUsedStep = step, EnrolledAt = Now,
    };

    private static NewMfaMethod Passkey(string userId, string? tenant, byte[] credentialId, long signCount = 0) => new()
    {
        TenantId = tenant, UserId = userId, Kind = MfaMethodKind.Passkey, Label = "iPhone",
        CredentialId = credentialId, PublicKey = [1, 2, 3], UserHandle = [9, 9], SignCount = signCount, EnrolledAt = Now,
    };

    private static byte[] NewCredentialId() => Guid.NewGuid().ToByteArray();

    private async Task<Guid> AddAndGetIdAsync(NewMfaMethod method)
    {
        Assert.Equal(MfaEnrolOutcome.Added, await Store.AddAsync(method));
        return (await Store.ListAsync(method.TenantId, method.UserId)).Single().Id;
    }

    [Fact]
    public async Task Migration_ShouldAdoptAPartiallyExistingSchema_WithoutALedger()
    {
        // A database that already holds one of the tables (an earlier partial run, or an adopter's own copy) but has
        // no migration ledger yet: the migration must create the rest instead of failing or skipping on the anchor.
        var database = $"adopt_{Guid.NewGuid():N}";
        await fixture.ExecuteAsync($"CREATE DATABASE {database}");
        var adoptString = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await using (var connection = new Npgsql.NpgsqlConnection(adoptString))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(
                "CREATE TABLE second_factor_audit (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, at timestamptz NOT NULL, tenant_id text NULL, target_user_id text NOT NULL, actor text NOT NULL, action text NOT NULL, method_kind text NULL, reason text NULL)",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await using var provider = new ServiceCollection().AddThemiaSecondFactorPostgres(adoptString).BuildServiceProvider();

        await using var check = new Npgsql.NpgsqlConnection(adoptString);
        await check.OpenAsync();
        await using var count = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_name LIKE 'second_factor_%'", check);
        Assert.Equal(5L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TryAdvanceTotpStep_ShouldAcceptFirstUse_WhenStepIsZero()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Totp(user, null));

        Assert.True(await Store.TryAdvanceTotpStepAsync(null, user, id, 0, Now));
    }

    [Fact]
    public async Task TryAdvanceTotpStep_ShouldRefuseTheEnrolmentConfirmCode()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Totp(user, null, step: 55_000));

        Assert.False(await Store.TryAdvanceTotpStepAsync(null, user, id, 55_000, Now));
        Assert.True(await Store.TryAdvanceTotpStepAsync(null, user, id, 55_001, Now));
    }

    [Fact]
    public async Task TryAdvanceTotpStep_ShouldLetExactlyOneWin_WhenSubmittedInParallel()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Totp(user, "t1"));

        var results = await Task.WhenAll(Enumerable.Range(0, Parallelism)
            .Select(_ => Store.TryAdvanceTotpStepAsync("t1", user, id, 777, Now)));

        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task TryUpdateSignCount_ShouldAcceptZeroAfterZero_ForSyncedPasskeys()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Passkey(user, null, NewCredentialId()));

        Assert.True(await Store.TryUpdateSignCountAsync(null, user, id, 0, Now));
        Assert.True(await Store.TryUpdateSignCountAsync(null, user, id, 0, Now));
    }

    [Fact]
    public async Task TryUpdateSignCount_ShouldRefuseRegression()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Passkey(user, null, NewCredentialId(), signCount: 10));

        Assert.False(await Store.TryUpdateSignCountAsync(null, user, id, 10, Now));
        Assert.False(await Store.TryUpdateSignCountAsync(null, user, id, 3, Now));
        Assert.True(await Store.TryUpdateSignCountAsync(null, user, id, 11, Now));
    }

    [Fact]
    public async Task AddAsync_ShouldRefuseTheSamePasskeyCredentialOnAnotherAccount()
    {
        var credential = NewCredentialId();
        await Store.AddAsync(Passkey(NewUser(), "t1", credential));

        var outcome = await Store.AddAsync(Passkey(NewUser(), "t2", credential));

        Assert.Equal(MfaEnrolOutcome.DuplicateCredential, outcome);
    }

    [Fact]
    public async Task FindPasskey_ShouldReturnTheOwnerAndUserHandle()
    {
        var user = NewUser();
        var credential = NewCredentialId();
        await Store.AddAsync(Passkey(user, null, credential));

        var found = await Store.FindPasskeyAsync(credential);

        Assert.NotNull(found);
        Assert.Equal(user, found.UserId);
        Assert.Null(found.TenantId);
        Assert.Equal(new byte[] { 9, 9 }, found.UserHandle);
    }

    [Fact]
    public async Task Methods_ShouldBeScopedToTenantAndUser_IncludingNullTenant()
    {
        var user = NewUser();
        var id = await AddAndGetIdAsync(Totp(user, null));

        Assert.Empty(await Store.ListAsync("other-tenant", user));
        Assert.False(await Store.RemoveAsync("other-tenant", user, id));
        Assert.False(await Store.RemoveAsync(null, NewUser(), id));
        Assert.Null(await Store.FindTotpAsync("other-tenant", user, id));
        Assert.Single(await Store.ListAsync(null, user));
        Assert.True(await Store.RemoveAsync(null, user, id));
    }

    [Fact]
    public async Task Migration_ShouldRejectATotpRowWithoutASecret()
    {
        var bad = Totp(NewUser(), null) with { SecretProtected = null };

        await Assert.ThrowsAnyAsync<Exception>(() => Store.AddAsync(bad));
    }
}
