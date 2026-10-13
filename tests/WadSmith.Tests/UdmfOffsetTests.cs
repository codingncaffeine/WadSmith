using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Tests;

/// <summary>
/// UDMF keeps a side's offsetx and offsety as integers: GZDoom logged "Integer value expected for key 'Offsetx'" over a
/// made map's fractional offset and dropped the fraction. The fraction now rides on each part's own offset.
/// </summary>
public sealed class UdmfOffsetTests
{
    private static MapModel Map(params MapSide[] sides)
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        m.Sectors.Add(new MapSector());
        m.Sides.AddRange(sides);
        return m;
    }

    [Theory]
    [InlineData(12.5, 0)]
    [InlineData(-3.25, 7.75)]
    [InlineData(12.3, -0.1)]
    [InlineData(0.5, 0)]
    public void AFractionalOffsetIsWrittenAsAnIntegerAndComesBackAsItWas(double x, double y)
    {
        var text = UdmfText.WriteString(MapConversion.ToUdmf(Map(new MapSide { OffsetX = x, OffsetY = y, Middle = "STARTAN2" })));
        var side = text[text.IndexOf("sidedef", StringComparison.Ordinal)..];
        side = side[..side.IndexOf('}')];
        // Every offsetx/offsety the file holds is an integer token.
        foreach (var line in side.Split('\n').Where(l => l.StartsWith("offsetx =", StringComparison.Ordinal) || l.StartsWith("offsety =", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(".", line, StringComparison.Ordinal);
        }

        var back = MapConversion.FromUdmf("MAP01", UdmfText.Parse(System.Text.Encoding.UTF8.GetBytes(text))).Sides.Single();
        Assert.Equal((x, y), (back.OffsetX, back.OffsetY));
        Assert.Empty(back.Extra);
    }

    [Fact]
    public void APartsOwnOffsetKeepsItsValueWithTheFractionAdded()
    {
        var s = new MapSide { OffsetX = 4.5, Middle = "STARTAN2" };
        s.Extra["offsetx_mid"] = UdmfValue.Float(2);
        var text = UdmfText.WriteString(MapConversion.ToUdmf(Map(s)));
        Assert.Contains("offsetx = 4;", text, StringComparison.Ordinal);
        Assert.Contains("offsetx_mid = 2.5;", text, StringComparison.Ordinal);
        Assert.Contains("offsetx_top = 0.5;", text, StringComparison.Ordinal);
    }
}
