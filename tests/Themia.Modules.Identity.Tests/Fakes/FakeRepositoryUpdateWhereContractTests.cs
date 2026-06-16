using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;
using Xunit;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>
/// Locks the uniform <see cref="IRepository{T,TKey}.UpdateWhereAsync"/> misuse contract shared by every peer
/// (EF, Dapper, this in-memory fake): zero <c>Set</c> calls and a non-member-access setter expression must fail
/// with the same exception type and message everywhere.
/// </summary>
public sealed class FakeRepositoryUpdateWhereContractTests
{
    private sealed class AllTokensSpec : Specification<RefreshToken>;

    private static FakeRepository<RefreshToken> NewRepo() =>
        new([], t => t.Id);

    [Fact]
    public async Task UpdateWhereAsync_WithNoSetCalls_ThrowsUniformInvalidOperation()
    {
        var repo = NewRepo();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateWhereAsync(new AllTokensSpec(), _ => { }));

        Assert.Equal("UpdateWhereAsync requires at least one Set(...) call.", ex.Message);
    }

    [Fact]
    public async Task UpdateWhereAsync_WithNonMemberSetterExpression_ThrowsUniformArgumentException()
    {
        var repo = NewRepo();

        // A method call rather than a direct property access — must be rejected with the uniform message.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => repo.UpdateWhereAsync(
                new AllTokensSpec(),
                set => set.Set(t => t.TokenHash.ToUpperInvariant(), "x")));

        Assert.StartsWith(
            "UpdateWhereAsync setters must be a direct property access, e.g. t => t.RevokedAt.",
            ex.Message);
    }
}
