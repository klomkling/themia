using Themia.Modules.Storage;
using Themia.Modules.Storage.Validation;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class DefaultFileValidatorTests
{
    private static DefaultFileValidator Validator(long max = 1000, params string[] allowed) =>
        new(new StorageModuleOptions { MaxObjectSizeBytes = max, AllowedContentTypes = allowed.Length == 0 ? null : allowed });

    [Fact]
    public void Within_size_and_allowed_type_is_valid()
    {
        var result = Validator(1000, "text/plain").Validate("a.txt", "text/plain", 500, null);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Over_size_is_invalid()
    {
        Assert.False(Validator(100).Validate("a.txt", "text/plain", 101, null).IsValid);
    }

    [Fact]
    public void Disallowed_type_is_invalid()
    {
        Assert.False(Validator(1000, "image/png").Validate("a.txt", "text/plain", 10, null).IsValid);
    }

    [Fact]
    public void Null_allowlist_allows_any_type()
    {
        Assert.True(Validator(1000).Validate("a.bin", "application/x-custom", 10, null).IsValid);
    }
}
