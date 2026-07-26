using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Themia.AspNetCore.DataProtection.Tests;

public class ThemiaXmlRepositoryTests
{
    [Fact]
    public void GetAllElements_ShouldSkipAnUnparseableRow_AndKeepTheRest()
    {
        // The whole point: one damaged row must not cost the application every other key. Returning nothing
        // here would fail every unprotect operation — auth cookies, antiforgery tokens, the lot.
        var store = new FakeKeyStore(["<key id=\"1\" />", "<not-xml", "<key id=\"2\" />"]);

        var elements = Repository(store).GetAllElements();

        Assert.Equal(2, elements.Count);
        Assert.Equal(["1", "2"], elements.Select(e => e.Attribute("id")!.Value));
    }

    [Fact]
    public void GetAllElements_ShouldReturnEmpty_WhenTheStoreIsEmpty()
    {
        // A fresh deployment has no keys yet; Data Protection generates the first one from an empty ring.
        Assert.Empty(Repository(new FakeKeyStore([])).GetAllElements());
    }

    [Fact]
    public void StoreElement_ShouldPersistTheFriendlyNameAndUnformattedXml()
    {
        var store = new FakeKeyStore([]);

        Repository(store).StoreElement(XElement.Parse("<key>\n  <child />\n</key>"), "key-1");

        var (friendlyName, xml) = Assert.Single(store.Stored);
        Assert.Equal("key-1", friendlyName);
        // Stored without indentation: the payload is data, not something a human diffs, and formatting would
        // only inflate every row.
        Assert.Equal("<key><child /></key>", xml);
    }

    [Fact]
    public void StoreElement_ShouldRejectANullElement()
    {
        Assert.Throws<ArgumentNullException>(() => Repository(new FakeKeyStore([])).StoreElement(null!, "key-1"));
    }

    private static ThemiaXmlRepository Repository(IDataProtectionKeyStore store) =>
        new(store, NullLogger<ThemiaXmlRepository>.Instance);

    private sealed class FakeKeyStore(IReadOnlyList<string> xml) : IDataProtectionKeyStore
    {
        public List<(string? FriendlyName, string Xml)> Stored { get; } = [];

        public IReadOnlyList<string> GetAllXml() => xml;

        public void StoreXml(string? friendlyName, string value) => Stored.Add((friendlyName, value));
    }
}
