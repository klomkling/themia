using Dapper;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResetStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    private IMfaResetStore Reset => fixture.Get<IMfaResetStore>();

    private IMfaMethodStore Methods => fixture.Get<IMfaMethodStore>();

    private static string NewUser() => Guid.NewGuid().ToString("D");

    private static NewMfaMethod Totp(string userId, string? tenant) => new()
    {
        TenantId = tenant, UserId = userId, Kind = MfaMethodKind.Totp, Label = "Authenticator",
        SecretProtected = "cipher", ProtectionScheme = "test-v1", EnrolledAt = Now,
    };

    private static string MethodsLockKey(string? tenant, string user) => $"second-factor-methods:{tenant}\n{user}";

    private async Task SeedAsync(string? tenant, string user)
    {
        Assert.Equal(MfaEnrolOutcome.Added, await Methods.AddAsync(Totp(user, tenant)));
        Assert.Equal(MfaEnrolOutcome.Added, await Methods.AddAsync(new NewMfaMethod
        {
            TenantId = tenant, UserId = user, Kind = MfaMethodKind.Passkey, Label = "Phone",
            CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1], UserHandle = [2], EnrolledAt = Now,
        }));
        await fixture.Get<IMfaRecoveryCodeStore>().ReplaceAsync(tenant, user, ["h1", "h2"], "test-v1", Now);
        await fixture.Get<IMfaCeremonyStore>().StoreAsync(
            Guid.NewGuid().ToString("N"), "{}", tenant, user, MfaCeremonyPurpose.Register, TimeSpan.FromMinutes(5));
        await fixture.Get<IMfaCeremonyStore>().StoreAsync(
            Guid.NewGuid().ToString("N"), "{}", tenant, user, MfaCeremonyPurpose.EnrolTotp, TimeSpan.FromMinutes(5));
        await fixture.Get<IMfaUserStateStore>().RecordFailureAsync(tenant, user, 10, TimeSpan.FromMinutes(5), Now);
        await fixture.Get<IMfaUserStateStore>().StartSpareNagAsync(tenant, user, Now);
    }

    private async Task<long> CountAsync(string sql, object args)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(sql, args);
    }

    private Task<long> CeremoniesAsync(string? tenant, string user) => CountAsync(
        "SELECT count(*) FROM second_factor_webauthn_ceremonies WHERE user_id = @user AND tenant_id IS NOT DISTINCT FROM @tenant", new { user, tenant });

    private Task<long> StateRowsAsync(string? tenant, string user) => CountAsync(
        "SELECT count(*) FROM second_factor_user_state WHERE user_id = @user AND tenant_id IS NOT DISTINCT FROM @tenant", new { user, tenant });

    private Task<long> AuditAsync(string user, string action) => CountAsync(
        "SELECT count(*) FROM second_factor_audit WHERE target_user_id = @user AND action = @action", new { user, action });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public async Task ResetAsync_ShouldRemoveEveryCredentialAndPendingState_AndAuditOnce(string? tenant)
    {
        var user = NewUser();
        var bystander = NewUser();
        await SeedAsync(tenant, user);
        await SeedAsync(tenant, bystander);

        var removed = await Reset.ResetAsync(tenant, user, "actor-1", "admin.reset", "lost phone", Now);

        Assert.Equal(2, removed);
        Assert.Empty(await Methods.ListAsync(tenant, user));
        Assert.Equal(0, await fixture.Get<IMfaRecoveryCodeStore>().CountUnusedAsync(tenant, user));
        Assert.Equal(0, await CeremoniesAsync(tenant, user));
        Assert.Equal(0, await StateRowsAsync(tenant, user));
        Assert.Equal(1, await AuditAsync(user, "admin.reset"));
        Assert.Equal(2, (await Methods.ListAsync(tenant, bystander)).Count);
        Assert.Equal(2, await CeremoniesAsync(tenant, bystander));
    }

    [Fact]
    public async Task ResetAsync_ShouldNotTouchTheSameUserIdInAnotherTenant()
    {
        var user = NewUser();
        await SeedAsync("tenant-a", user);
        await SeedAsync("tenant-b", user);

        await Reset.ResetAsync("tenant-a", user, "actor-1", "admin.reset", "lost phone", Now);

        Assert.Empty(await Methods.ListAsync("tenant-a", user));
        Assert.Equal(2, (await Methods.ListAsync("tenant-b", user)).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResetAsync_ShouldRefuseABlankReason_AndChangeNothing(string? reason)
    {
        var user = NewUser();
        await SeedAsync("tenant-a", user);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Reset.ResetAsync("tenant-a", user, "actor-1", "admin.reset", reason!, Now));

        Assert.Equal(2, (await Methods.ListAsync("tenant-a", user)).Count);
        Assert.Equal(0, await AuditAsync(user, "admin.reset"));
    }

    [Fact]
    public async Task ResetAsync_ShouldRollBackTheDeletes_WhenTheAuditInsertFails()
    {
        var user = NewUser();
        await SeedAsync("tenant-a", user);
        await fixture.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION tmp_fail_audit() RETURNS trigger AS $f$ BEGIN RAISE EXCEPTION 'forced audit failure'; END; $f$ LANGUAGE plpgsql;
            CREATE TRIGGER tmp_fail_audit_trigger BEFORE INSERT ON second_factor_audit FOR EACH ROW EXECUTE FUNCTION tmp_fail_audit();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<PostgresException>(() => Reset.ResetAsync("tenant-a", user, "actor-1", "admin.reset", "lost phone", Now));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER tmp_fail_audit_trigger ON second_factor_audit; DROP FUNCTION tmp_fail_audit();");
        }

        Assert.Equal(2, (await Methods.ListAsync("tenant-a", user)).Count);
        Assert.Equal(2, await CeremoniesAsync("tenant-a", user));
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public async Task ResetAsync_ShouldWaitForTheMethodsLock(string? tenant)
    {
        var user = NewUser();
        await SeedAsync(tenant, user);

        await using var holder = await HoldAsync(MethodsLockKey(tenant, user));
        var reset = Reset.ResetAsync(tenant, user, "actor-1", "admin.reset", "lost phone", Now);
        await WaitUntilParkedAsync();
        Assert.False(reset.IsCompleted);

        await holder.ReleaseAsync();
        Assert.Equal(2, await reset.WaitAsync(LockWait));
    }

    [Fact]
    public async Task ResetAsync_ShouldWaitForTheRecoveryLock()
    {
        var user = NewUser();
        await SeedAsync("tenant-a", user);

        await using var holder = await HoldAsync($"second-factor-recovery:tenant-a\n{user}");
        var reset = Reset.ResetAsync("tenant-a", user, "actor-1", "admin.reset", "lost phone", Now);
        await WaitUntilParkedAsync();
        Assert.False(reset.IsCompleted);

        await holder.ReleaseAsync();
        await reset.WaitAsync(LockWait);
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public async Task AddAsync_ShouldWaitForTheMethodsLock_SoAnEnrolCannotSlipPastAReset(string? tenant)
    {
        var user = NewUser();

        await using var holder = await HoldAsync(MethodsLockKey(tenant, user));
        var add = Methods.AddAsync(Totp(user, tenant));
        await WaitUntilParkedAsync();
        Assert.False(add.IsCompleted);

        await holder.ReleaseAsync();
        Assert.Equal(MfaEnrolOutcome.Added, await add.WaitAsync(LockWait));
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public async Task BreakGlass_ShouldResetOneUser_AndAuditTheDatabaseSessionUser(string? tenant)
    {
        var user = NewUser();
        var bystander = NewUser();
        await SeedAsync(tenant, user);
        await SeedAsync(tenant, bystander);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        var removed = await connection.ExecuteScalarAsync<int>(
            "SELECT second_factor_break_glass_reset(@tenant, @user, @reason)", new { tenant, user, reason = "operator ticket 42" });

        Assert.Equal(2, removed);
        Assert.Empty(await Methods.ListAsync(tenant, user));
        Assert.Equal(0, await CeremoniesAsync(tenant, user));
        Assert.Equal(2, (await Methods.ListAsync(tenant, bystander)).Count);
        var actor = await connection.ExecuteScalarAsync<string>(
            "SELECT actor FROM second_factor_audit WHERE target_user_id = @user AND action = 'break_glass.reset'", new { user });
        Assert.Equal("db:" + await connection.ExecuteScalarAsync<string>("SELECT session_user"), actor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BreakGlass_ShouldRaise_WhenTheReasonIsMissing(string? reason)
    {
        var user = NewUser();
        await SeedAsync(null, user);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteScalarAsync<int>("SELECT second_factor_break_glass_reset(NULL, @user, @reason)", new { user, reason }));

        Assert.Equal(2, (await Methods.ListAsync(null, user)).Count);
    }

    [Fact]
    public async Task BreakGlass_ShouldWaitForTheLock_ForAPlatformUser()
    {
        var user = NewUser();
        await SeedAsync(null, user);

        await using var holder = await HoldAsync(MethodsLockKey(null, user));
        var call = Task.Run(async () =>
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            return await connection.ExecuteScalarAsync<int>("SELECT second_factor_break_glass_reset(NULL, @user, 'ticket 7')", new { user });
        });
        await WaitUntilParkedAsync();
        Assert.False(call.IsCompleted);

        await holder.ReleaseAsync();
        Assert.Equal(2, await call.WaitAsync(LockWait));
    }

    [Fact]
    public async Task BreakGlass_ShouldBeRefused_ForAnOrdinaryRole()
    {
        var role = "sf_plain_" + Guid.NewGuid().ToString("N")[..8];
        await fixture.ExecuteAsync($"CREATE ROLE {role}; GRANT USAGE ON SCHEMA public TO {role};");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"SET ROLE {role}");

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteScalarAsync<int>("SELECT second_factor_break_glass_reset(NULL, 'u', 'ticket 1')"));

        Assert.Equal("42501", error.SqlState);
    }

    // The grants the runbook tells a DBA to give the operator role. Keep this list and the runbook in step.
    private const string RunbookGrants = """
        GRANT USAGE ON SCHEMA public TO {0};
        GRANT EXECUTE ON FUNCTION second_factor_break_glass_reset(text, text, text) TO {0};
        GRANT EXECUTE ON FUNCTION second_factor_reset_user(text, text, text, text, text, timestamptz) TO {0};
        GRANT SELECT, DELETE ON second_factor_methods, second_factor_recovery_codes, second_factor_webauthn_ceremonies, second_factor_user_state TO {0};
        GRANT INSERT ON second_factor_audit TO {0};
        """;

    [Fact]
    public async Task BreakGlass_ShouldReset_ForARoleGrantedExactlyAsTheRunbookSays()
    {
        var user = NewUser();
        await SeedAsync(null, user);
        var role = "sf_ops_" + Guid.NewGuid().ToString("N")[..8];
        await fixture.ExecuteAsync($"CREATE ROLE {role}; " + string.Format(RunbookGrants, role));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"SET ROLE {role}");

        var removed = await connection.ExecuteScalarAsync<int>(
            "SELECT second_factor_break_glass_reset(NULL, @user, 'ticket 1')", new { user });

        Assert.Equal(2, removed);
        Assert.Equal(1, await AuditAsync(user, "break_glass.reset"));
    }

    [Fact]
    public async Task BreakGlass_ShouldBeRefused_ForARoleGrantedOnlyTheBreakGlassFunction()
    {
        var role = "sf_half_" + Guid.NewGuid().ToString("N")[..8];
        await fixture.ExecuteAsync(
            $"CREATE ROLE {role}; GRANT USAGE ON SCHEMA public TO {role}; " +
            $"GRANT EXECUTE ON FUNCTION second_factor_break_glass_reset(text, text, text) TO {role};");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"SET ROLE {role}");

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteScalarAsync<int>("SELECT second_factor_break_glass_reset(NULL, 'u', 'ticket 1')"));

        Assert.Equal("42501", error.SqlState);
    }

    private async Task<LockHolder> HoldAsync(string key)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key }, transaction);
        return new LockHolder(connection, transaction);
    }

    private async Task WaitUntilParkedAsync()
    {
        using var timeout = new CancellationTokenSource(LockWait);
        while (await CountAsync("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted", new { }) == 0)
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public Task ReleaseAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
