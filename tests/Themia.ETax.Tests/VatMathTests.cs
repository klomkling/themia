using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Themia.ETax.Tests;

public class VatMathTests
{
    private static readonly JsonElement[] Cases = LoadCases();

    private static JsonElement[] LoadCases()
    {
        using var stream = typeof(VatMathTests).Assembly.GetManifestResourceStream("vatmath-parity.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public void ParityVectors_ShouldBeLoaded_WithMidpointCases()
    {
        Assert.True(Cases.Length > 100);
        Assert.True(Cases.Count(c => c.GetProperty("midpoint").GetBoolean()) >= 50);
    }

    [Fact]
    public void ParityVectors_ShouldCoverMidpoints_AtTheDefaultAndFractionalRates()
    {
        bool HasMidpoint(string op, int bps) =>
            Cases.Any(c => c.GetProperty("op").GetString() == op && c.GetProperty("rateBps").GetInt32() == bps && c.GetProperty("midpoint").GetBoolean());

        Assert.True(HasMidpoint("addVat", 700));
        Assert.True(HasMidpoint("addVat", 750));
        Assert.True(HasMidpoint("split", 16)); // the only rate here where Split can land on a midpoint
    }

    [Fact]
    public void ParityVectorFile_ShouldBeRecomputedFromTheOpsezyFormula_ForEveryCase()
    {
        foreach (var c in Cases)
        {
            var percent = decimal.Parse(c.GetProperty("ratePercent").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal((long)(percent * 100m), c.GetProperty("rateBps").GetInt64());

            if (c.GetProperty("op").GetString() == "split")
            {
                var incl = decimal.Parse(c.GetProperty("inclBaht").GetString()!, CultureInfo.InvariantCulture);
                var (ex, vat) = OpsezyDecimalReference.Split(incl, percent);
                Assert.Equal(ex, decimal.Parse(c.GetProperty("exVatBaht").GetString()!, CultureInfo.InvariantCulture));
                Assert.Equal(vat, decimal.Parse(c.GetProperty("vatBaht").GetString()!, CultureInfo.InvariantCulture));
                Assert.Equal(OpsezyDecimalReference.IsMidpoint(incl * 100m / (100m + percent)), c.GetProperty("midpoint").GetBoolean());
            }
            else
            {
                var ex = decimal.Parse(c.GetProperty("exVatBaht").GetString()!, CultureInfo.InvariantCulture);
                Assert.Equal(OpsezyDecimalReference.AddVat(ex, percent), decimal.Parse(c.GetProperty("inclBaht").GetString()!, CultureInfo.InvariantCulture));
                Assert.Equal(OpsezyDecimalReference.IsMidpoint(ex * (100m + percent) / 100m), c.GetProperty("midpoint").GetBoolean());
            }
        }
    }

    [Fact]
    public void Split_ShouldMatchOpsezyDecimalVectors_ForEveryCase()
    {
        foreach (var c in Cases.Where(c => c.GetProperty("op").GetString() == "split"))
        {
            var actual = VatMath.Split(c.GetProperty("inclSatang").GetInt64(), c.GetProperty("rateBps").GetInt32());
            Assert.Equal(c.GetProperty("exVatSatang").GetInt64(), actual.ExVatSatang);
            Assert.Equal(c.GetProperty("vatSatang").GetInt64(), actual.VatSatang);
        }
    }

    [Fact]
    public void AddVat_ShouldMatchOpsezyDecimalVectors_ForEveryCase()
    {
        foreach (var c in Cases.Where(c => c.GetProperty("op").GetString() == "addVat"))
        {
            var actual = VatMath.AddVat(c.GetProperty("exVatSatang").GetInt64(), c.GetProperty("rateBps").GetInt32());
            Assert.Equal(c.GetProperty("inclSatang").GetInt64(), actual.InclVatSatang);
            Assert.Equal(c.GetProperty("exVatSatang").GetInt64(), actual.ExVatSatang);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(700)]
    [InlineData(750)]
    [InlineData(1525)]
    public void Sweep_ShouldMatchDecimalReference_AndAddBackExactly(int rateBps)
    {
        var percent = rateBps / 100m;
        for (long satang = 0; satang <= 1_000_000; satang++)
        {
            var split = VatMath.Split(satang, rateBps);
            var baht = satang / 100m;
            Assert.Equal((long)(OpsezyDecimalReference.Split(baht, percent).ExVat * 100m), split.ExVatSatang);
            Assert.Equal(satang, split.ExVatSatang + split.VatSatang);

            var add = VatMath.AddVat(satang, rateBps);
            Assert.Equal((long)(OpsezyDecimalReference.AddVat(baht, percent) * 100m), add.InclVatSatang);
        }
    }

    [Fact]
    public void Split_ShouldNotOverflow_ForVeryLargeAmounts()
    {
        var split = VatMath.Split(long.MaxValue / 2, 700);
        Assert.Equal(long.MaxValue / 2, split.ExVatSatang + split.VatSatang);
    }

    [Fact]
    public void AddVat_ShouldThrowOverflow_WhenResultExceedsLong()
    {
        Assert.Throws<OverflowException>(() => VatMath.AddVat(long.MaxValue, 700));
    }

    [Theory]
    [InlineData(-1, 700)]
    [InlineData(100, -1)]
    [InlineData(100, 10_001)]
    public void Methods_ShouldReject_NegativeAmountOrOutOfRangeRate(long satang, int bps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VatMath.Split(satang, bps));
        Assert.Throws<ArgumentOutOfRangeException>(() => VatMath.AddVat(satang, bps));
    }
}
