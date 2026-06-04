using Themia.Mediator.Abstractions;
using Themia.Mediator.Configuration;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Tests.Caching;

public sealed class DefaultCacheKeyFactoryTests
{
    private readonly DefaultCacheKeyFactory _factory = new();
    private readonly MediatorCachingOptions _options = new()
    {
        KnownTypeSuffixes = ["Query", "Command", "Request"],
        KnownVerbPrefixes = ["Get", "List", "Find", "Create", "Update", "Delete"]
    };

    [Fact]
    public void Should_generate_consistent_keys_for_same_request()
    {
        // Arrange
        var request1 = new TestRequest("foo", 42);
        var request2 = new TestRequest("foo", 42);

        // Act
        var key1 = _factory.CreateKey(request1);
        var key2 = _factory.CreateKey(request2);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Should_generate_different_keys_for_different_request_data()
    {
        // Arrange
        var request1 = new TestRequest("foo", 42);
        var request2 = new TestRequest("bar", 42);

        // Act
        var key1 = _factory.CreateKey(request1);
        var key2 = _factory.CreateKey(request2);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Should_use_custom_key_when_ICacheKeyProvider_implemented()
    {
        // Arrange
        var request = new CustomKeyRequest("test");

        // Act
        var key = _factory.CreateKey(request);

        // Assert
        Assert.Equal("custom-key:test", key);
    }

    [Fact]
    public void Should_throw_when_custom_key_is_null_or_empty()
    {
        // Arrange
        var request = new InvalidKeyRequest();

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => _factory.CreateKey(request));
    }

    [Fact]
    public void Should_create_type_prefix_from_type_fullname()
    {
        // Act
        var prefix = _factory.CreateTypePrefix(typeof(TestRequest));

        // Assert
        Assert.StartsWith("QueryType:", prefix);
        Assert.Contains(nameof(TestRequest), prefix);
    }

    [Fact]
    public void Should_create_scope_root_from_GetOrderQuery()
    {
        // Act
        var scopeRoot = _factory.CreateScopeRoot(typeof(GetOrderQuery), _options);

        // Assert
        Assert.Equal("Scope:Order", scopeRoot);
    }

    [Fact]
    public void Should_create_scope_root_from_UpdateOrderCommand()
    {
        // Act
        var scopeRoot = _factory.CreateScopeRoot(typeof(UpdateOrderCommand), _options);

        // Assert
        Assert.Equal("Scope:Order", scopeRoot);
    }

    [Fact]
    public void Should_create_scope_root_from_ListOrdersQuery()
    {
        // Act
        var scopeRoot = _factory.CreateScopeRoot(typeof(ListOrdersQuery), _options);

        // Assert
        Assert.Equal("Scope:Orders", scopeRoot);
    }

    [Fact]
    public void Should_return_null_scope_when_no_pattern_matches()
    {
        // Act
        var scopeRoot = _factory.CreateScopeRoot(typeof(WeirdRequest), _options);

        // Assert
        Assert.Null(scopeRoot);
    }

    // Test types
    public sealed record TestRequest(string Name, int Value) : IQuery<string>;

    public sealed record CustomKeyRequest(string Id) : IQuery<string>, ICacheKeyProvider
    {
        public string GetCacheKey() => $"custom-key:{Id}";
        public string? GetCacheKeyPrefix() => "custom-key:";
    }

    public sealed record InvalidKeyRequest : IQuery<string>, ICacheKeyProvider
    {
        public string GetCacheKey() => "";
        public string? GetCacheKeyPrefix() => null;
    }

    public sealed record GetOrderQuery(int OrderId) : IQuery<string>;
    public sealed record UpdateOrderCommand(int OrderId) : ICommand<bool>;
    public sealed record ListOrdersQuery() : IQuery<string>;
    public sealed record WeirdRequest() : IQuery<string>;
}
