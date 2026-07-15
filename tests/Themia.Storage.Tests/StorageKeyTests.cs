using Themia.Storage;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class StorageKeyTests
{
    [Theory]
    [InlineData("public/t1/a.jpg", true)]
    [InlineData("t1/a.jpg", false)]
    [InlineData("publicity/t1/a.jpg", false)] // prefix match must be on the whole first SEGMENT
    public void IsPublic_matches_only_the_whole_first_segment(string key, bool expected)
    {
        Assert.Equal(expected, StorageKey.IsPublic(key));
    }

    [Fact]
    public void StripVisibilityPrefix_removes_the_public_segment_and_leaves_private_keys_alone()
    {
        Assert.Equal("t1/a.jpg", StorageKey.StripVisibilityPrefix("public/t1/a.jpg"));
        Assert.Equal("t1/a.jpg", StorageKey.StripVisibilityPrefix("t1/a.jpg"));
    }

    [Fact]
    public void StoragePutOptions_defaults_to_private()
    {
        Assert.Equal(StorageVisibility.Private, new StoragePutOptions("image/png").Visibility);
    }
}
