using System.Xml.Linq;
using Xunit;

namespace Themia.AspNetCore.DataProtection.Tests;

public class ThemiaXmlRepositoryTests
{
    [Fact]
    public void GetAllElements_ShouldReturnEveryStoredElement()
    {
        var store = new FakeKeyStore([Row(1, "<key id=\"1\" />"), Row(2, "<key id=\"2\" />")]);

        var elements = new ThemiaXmlRepository(store).GetAllElements();

        Assert.Equal(["1", "2"], elements.Select(e => e.Attribute("id")!.Value));
    }

    [Fact]
    public void GetAllElements_ShouldThrow_WhenARowIsNotWellFormed()
    {
        // Fails closed on purpose. Skipping the row would be unsafe in two directions: the row may be a
        // <revocation> rather than a <key>, so dropping it silently reinstates revoked key material; and if
        // every row is unreadable the survivors are an EMPTY ring, which Data Protection cannot tell from a
        // fresh deployment — it mints a new key and signs out every user while reporting healthy.
        var store = new FakeKeyStore([Row(1, "<key id=\"1\" />"), Row(7, "<not-xml")]);

        var ex = Assert.Throws<InvalidOperationException>(() => new ThemiaXmlRepository(store).GetAllElements());

        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void GetAllElements_ShouldThrow_WhenARowHasNoXml()
    {
        // A pre-existing table with a nullable xml column: XElement.Parse(null) would throw
        // ArgumentNullException, which is not an XmlException and would escape any parse-only guard.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ThemiaXmlRepository(new FakeKeyStore([Row(4, null)])).GetAllElements());

        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void GetAllElements_ShouldReturnEmpty_WhenTheStoreIsEmpty()
    {
        // A genuinely empty table is the one case where an empty ring is correct: a fresh deployment.
        Assert.Empty(new ThemiaXmlRepository(new FakeKeyStore([])).GetAllElements());
    }

    [Fact]
    public void StoreElement_ShouldPersistTheFriendlyNameAndUnformattedXml()
    {
        var store = new FakeKeyStore([]);

        new ThemiaXmlRepository(store).StoreElement(XElement.Parse("<key>\n  <child />\n</key>"), "key-1");

        var (friendlyName, xml) = Assert.Single(store.Stored);
        Assert.Equal("key-1", friendlyName);
        Assert.Equal("<key><child /></key>", xml);
    }

    [Fact]
    public void StoreElement_ShouldRejectANullElement()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ThemiaXmlRepository(new FakeKeyStore([])).StoreElement(null!, "key-1"));
    }

#if NET10_0_OR_GREATER
    [Fact]
    public void DeleteElements_ShouldDeleteOnlyTheChosenRows_InIncreasingDeletionOrder()
    {
        var store = new FakeKeyStore([Row(1, "<key id=\"1\" />"), Row(2, "<key id=\"2\" />"), Row(3, "<key id=\"3\" />")]);

        var deletedAll = new ThemiaXmlRepository(store).DeleteElements(elements =>
        {
            foreach (var e in elements)
            {
                var id = e.Element.Attribute("id")!.Value;
                // Chosen out of order to prove the implementation sorts rather than following the snapshot.
                if (id == "3") e.DeletionOrder = 1;
                if (id == "1") e.DeletionOrder = 2;
            }
        });

        Assert.True(deletedAll);
        Assert.Equal([3L, 1L], store.Deleted);
    }

    [Fact]
    public void DeleteElements_ShouldStopAndReportFailure_WhenADeleteDoesNotRemoveARow()
    {
        // Contract: "If any deletion fails, the remaining deletions MUST be skipped."
        var store = new FakeKeyStore([Row(1, "<key id=\"1\" />"), Row(2, "<key id=\"2\" />")]) { FailDeleteOf = 1 };

        var deletedAll = new ThemiaXmlRepository(store).DeleteElements(elements =>
        {
            foreach (var e in elements)
                e.DeletionOrder = int.Parse(e.Element.Attribute("id")!.Value);
        });

        Assert.False(deletedAll);
        Assert.Equal([1L], store.Deleted);
    }
#endif

    private static DataProtectionKeyRecord Row(long id, string? xml) => new(id, xml);

    private sealed class FakeKeyStore(IReadOnlyList<DataProtectionKeyRecord> rows) : IDataProtectionKeyStore
    {
        public List<(string? FriendlyName, string Xml)> Stored { get; } = [];

        public List<long> Deleted { get; } = [];

        public long? FailDeleteOf { get; init; }

        public IReadOnlyList<DataProtectionKeyRecord> GetAll() => rows;

        public void StoreXml(string? friendlyName, string xml) => Stored.Add((friendlyName, xml));

        public bool Delete(long id)
        {
            Deleted.Add(id);
            return id != FailDeleteOf;
        }
    }
}
