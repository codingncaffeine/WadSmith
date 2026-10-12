using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;

namespace WadSmith.Tests;

public sealed class ClassicMapTests
{
    /// <summary>Base games and add-ons whose maps are in the Doom or Hexen format (Doom 64 is out of scope).</summary>
    public static TheoryData<string> MapWads()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "DOOM.WAD", "DOOM2.WAD", "PLUTONIA.WAD", "TNT.WAD", "HERETIC.WAD", "HEXEN.WAD", "STRIFE1.WAD", "SIGIL_v1_21.wad", "SIGIL_II_V1_0.WAD", "freedoom1.wad", "freedoom2.wad" })
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MapWads))]
    public void EveryMapRoundTripsByteForByte(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var maps = MapLocator.Find(wad);
        Assert.NotEmpty(maps);
        foreach (var map in maps)
        {
            var classic = ClassicMap.Read(map);
            Assert.NotEmpty(classic.Lines);
            var written = classic.WriteLumps();
            Assert.Equal(map.Lumps.Select(l => l.Name), written.Select(l => l.Name));
            for (var i = 0; i < map.Lumps.Count; i++)
            {
                Assert.True(map.Lumps[i].Data.AsSpan().SequenceEqual(written[i].Data), $"{fileName} {map.Name} {map.Lumps[i].Name} differs after writing");
            }
        }
    }

    [Fact]
    public void HexenMapsAreRecognisedByTheirBehaviorLump()
    {
        var wad = WadContainer.Open(GameFiles.Require("HEXEN.WAD"));
        var maps = MapLocator.Find(wad);
        Assert.All(maps, m => Assert.Equal(MapFormat.Hexen, m.Format));
        Assert.Contains(maps, m => m.Name == "MAP01");
    }

    [Fact]
    public void ARecordListThatIsNotWholeIsCorrupt()
    {
        var lumps = new MapLumps("MAP01", MapFormat.Doom, [("THINGS", new byte[11])], WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [])));
        var ex = Assert.Throws<WadSmith.Core.CorruptDataException>(() => ClassicMap.Read(lumps));
        Assert.Equal("MAP01's THINGS lump is 11 bytes, which is not a whole number of 10-byte records.", ex.Message);
    }
}
