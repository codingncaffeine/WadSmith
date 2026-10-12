using System.Globalization;
using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Tests;

public sealed class UdmfTests
{
    private const string Sample = """
        // A comment before anything.
        namespace = "zdoom";
        /* a block
           comment */
        Vertex { x = 1.5; y = -64; }
        linedef
        {
          v1 = 0; v2 = 010; sidefront = 0x1F;
          Blocking = true; comment = "say \"hi\" \\ bye";
          user_score = -2.5e1;
        }
        customblock { anything = false; }
        """;

    [Fact]
    public void ParsesEveryKindOfToken()
    {
        var doc = UdmfText.Parse(Sample);
        Assert.Equal("zdoom", doc.Namespace);
        Assert.Equal(["vertex", "linedef", "customblock"], doc.Blocks.Select(b => b.Type));
        var v = doc.Blocks[0].Fields;
        Assert.Equal(1.5, v["x"].AsFloat());
        Assert.Equal(UdmfValueKind.Integer, v["y"].Kind);
        var l = doc.Blocks[1].Fields;
        Assert.Equal(8, l["v2"].AsInt());
        Assert.Equal(31, l["sidefront"].AsInt());
        Assert.True(l["blocking"].AsBool());
        Assert.Equal("say \"hi\" \\ bye", l["comment"].AsString());
        Assert.Equal(-25.0, l["user_score"].AsFloat());
    }

    [Fact]
    public void WritingAndParsingAgainGivesTheSameText()
    {
        var once = UdmfText.WriteString(UdmfText.Parse(Sample));
        var twice = UdmfText.WriteString(UdmfText.Parse(once));
        Assert.Equal(once, twice);
        Assert.Contains("comment = \"say \\\"hi\\\" \\\\ bye\";", once, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linedef { v1 = 0 }", "TEXTMAP line 1: expected ; after the value of \"v1\".")]
    [InlineData("thing\n{\nx = ;\n}", "TEXTMAP line 3: \"x\" has no value.")]
    [InlineData("sector { texturefloor = \"FLAT1; }", "TEXTMAP line 1: a quoted string is never closed.")]
    [InlineData("vertex { x = 1.0;", "TEXTMAP line 1: the \"vertex\" block is never closed with }.")]
    [InlineData("thing { x = maybe; }", "TEXTMAP line 1: \"maybe\" is not a number, a quoted string, true or false (the value of \"x\").")]
    public void BrokenTextIsReportedWithItsLine(string text, string message)
    {
        var ex = Assert.Throws<CorruptDataException>(() => UdmfText.Parse(text));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void NumbersAreWrittenTheSameWayInEveryLanguage()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var doc = new UdmfDocument { Namespace = "zdoom" };
            var b = new UdmfBlock("vertex");
            b.Fields["x"] = 1.5;
            b.Fields["y"] = 64.0;
            doc.Blocks.Add(b);
            var text = UdmfText.WriteString(doc);
            Assert.Contains("x = 1.5;", text, StringComparison.Ordinal);
            Assert.Contains("y = 64.0;", text, StringComparison.Ordinal);
            Assert.Equal(1.5, UdmfText.Parse(text).Blocks[0].Fields["x"].AsFloat());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    public static TheoryData<string, string> GameMaps() => new()
    {
        { "DOOM.WAD", "doom" }, { "DOOM2.WAD", "doom" }, { "PLUTONIA.WAD", "doom" }, { "TNT.WAD", "doom" },
        { "SIGIL_v1_21.wad", "doom" }, { "SIGIL_II_V1_0.WAD", "doom" }, { "freedoom1.wad", "doom" }, { "freedoom2.wad", "doom" },
        { "HERETIC.WAD", "heretic" }, { "HEXEN.WAD", "hexen" }, { "STRIFE1.WAD", "strife" },
    };

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryClassicMapSurvivesTheModelAndUdmf(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        foreach (var lumps in MapLocator.Find(wad))
        {
            var model = MapConversion.Read(lumps, game);

            // Through the model and back to the classic format, unchanged: byte-identical.
            var back = MapConversion.ToClassic(model, lumps.Format).WriteLumps();
            for (var i = 0; i < lumps.Lumps.Count; i++)
            {
                Assert.True(lumps.Lumps[i].Data.AsSpan().SequenceEqual(back[i].Data), $"{fileName} {lumps.Name} {lumps.Lumps[i].Name} changed through the model");
            }

            // Out to UDMF text and in again: the same map.
            var text1 = UdmfText.WriteString(MapConversion.ToUdmf(model));
            var reread = MapConversion.FromUdmf(model.Name, UdmfText.Parse(text1));
            var text2 = UdmfText.WriteString(MapConversion.ToUdmf(reread));
            Assert.True(text1 == text2, $"{fileName} {lumps.Name} reads back differently from its UDMF text");

            // And from that UDMF back to the classic format: the same meaning again.
            var classic2 = MapConversion.ToClassic(reread, lumps.Format);
            var text3 = UdmfText.WriteString(MapConversion.ToUdmf(MapConversion.FromClassic(classic2, model.Namespace)));
            Assert.True(text1 == text3, $"{fileName} {lumps.Name} means something else after UDMF and back to {lumps.Format} format");
            Assert.Equal(model.Lines.Count, reread.Lines.Count);
            Assert.Equal(model.Things.Count, reread.Things.Count);
        }
    }

    [Fact]
    public void DoomThingFlagsBecomeNamedFlags()
    {
        // Easy + hard, deaf, multiplayer only.
        var c = new Core.Maps.Classic.ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        c.Things.Add(new Core.Maps.Classic.ClassicThing(0, 0, 90, 3001, 0x0001 | 0x0004 | 0x0008 | 0x0010));
        var t = MapConversion.FromClassic(c, "doom").Things[0];
        Assert.True(t.Flag("skill1") && t.Flag("skill2") && t.Flag("skill4") && t.Flag("skill5") && t.Flag("ambush"));
        Assert.False(t.Flag("skill3") || t.Flag("single"));
        Assert.True(t.Flag("coop") && t.Flag("dm"));
    }

    [Fact]
    public void TheBadEditorBitLimitsDoomThingsToTheVanillaFlags()
    {
        var c = new Core.Maps.Classic.ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        // Bit 8 set: Boom ignores "not in deathmatch", "not in co-op" and "friendly".
        c.Things.Add(new Core.Maps.Classic.ClassicThing(0, 0, 0, 3001, 0x0100 | 0x0020 | 0x0040 | 0x0080 | 0x0007));
        var t = MapConversion.FromClassic(c, "doom").Things[0];
        Assert.True(t.Flag("dm") && t.Flag("coop"));
        Assert.False(t.Flag("friend"));
    }

    [Fact]
    public void HexenActivationBecomesTriggerFlags()
    {
        var c = new Core.Maps.Classic.ClassicMap { Name = "MAP01", Format = MapFormat.Hexen };
        // Use, repeatable, special 12 (Door_Raise).
        c.Lines.Add(new Core.Maps.Classic.ClassicLine(0, 1, (1 << 10) | 0x0200, 12, 0, 0, 0xFFFF, Arg0: 5));
        var l = MapConversion.FromClassic(c, "hexen").Lines[0];
        Assert.True(l.Flag("playeruse") && l.Flag("repeatspecial"));
        Assert.Equal(-1, l.Back);
        Assert.Equal(5, l.Args[0]);
    }
}
