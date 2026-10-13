using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The way back to a classic map (6b): the engine's table inverted, so what the tools make round-trips through a vanilla file.</summary>
public sealed class ClassicTranslationTests
{
    private static XlatTranslator Doom() => XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");

    /// <summary>The map's text with the marks the forward translation adds to every classic map set aside: dropactors on each room, and monsters' use of a wall named the engine's way.</summary>
    private static string Udmf(MapModel m)
    {
        foreach (var s in m.Sectors) s.Extra.Remove("dropactors");
        foreach (var l in m.Lines)
        {
            if (l.Flag("monsteruse") || l.Flag("monsteractivate"))
            {
                l.Extra.Remove("monsteruse");
                l.SetFlag("monsteractivate", true);
            }
        }

        // The fields of each block in a fixed order: the classic flags come back in their bit order, which says nothing new.
        var text = UdmfText.WriteString(MapConversion.ToUdmf(m));
        return string.Join("}", text.Split('}').Select(block => string.Join("\n", block.Split('\n').Order(StringComparer.Ordinal))));
    }

    [Fact]
    public void TheToolsObjectsHaveTheirClassicTypesAndRoundTripThroughAVanillaFile()
    {
        var xlat = Doom();
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room 0 0 256 256\nroom 256 96 384 160\nroom 384 0 640 256\nstart 128 128\ndoor 256 128\nexit 0 128\nsecret 512 128", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var model = map.ToModel();

        // The door's own faces: Doom's DR door, type 1, no tag. The exit switch: type 11.
        var doors = model.Lines.Where(l => l.Special == 12).ToList();
        Assert.NotEmpty(doors);
        Assert.All(doors, l => Assert.Equal((1, 0), ClassicTranslation.TypeOf(l, xlat)));
        var exit = model.Lines.Single(l => l.Special == 243);
        Assert.Equal((11, 0), ClassicTranslation.TypeOf(exit, xlat));
        Assert.All(model.Lines.Where(l => l.Special == 0), l => Assert.Equal((0, 0), ClassicTranslation.TypeOf(l, xlat)));

        // Back to the classic namespace, written as a vanilla file, read again and translated forward: the same map.
        var report = new List<NotClassic>();
        var classic = ClassicTranslation.ToClassicNamespace(model, xlat, "doom", report);
        Assert.Empty(report);
        Assert.Equal(1, classic.Lines.Count(l => l.Special == 11));
        var lumps = MapConversion.ToClassic(classic, MapFormat.Doom).WriteLumps();
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [("MAP01", ReadOnlyMemory<byte>.Empty), .. lumps.Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))]));
        var read = MapConversion.Read(MapLocator.Find(wad).Single(), "doom");
        var back = MapTranslation.ToZDoom(read, xlat, []);
        Assert.Equal(Udmf(model), Udmf(back));
    }

    [Fact]
    public void WhatHasNoClassicTypeIsReportedAndWrittenPlain()
    {
        var xlat = Doom();
        var model = StarterRoom.Create("MAP01", "doom");
        var wall = model.Lines[0];
        wall.Special = 12;
        wall.Args[1] = 40;   // a door speed no classic type has
        wall.SetFlag("playeruse", true);
        wall.SetFlag("repeatspecial", true);
        model.Sectors[0].Special = 65;   // dLight_Flicker: classic type 1
        var report = new List<NotClassic>();
        var classic = ClassicTranslation.ToClassicNamespace(model, xlat, "doom", report);
        var only = Assert.Single(report);
        Assert.Equal(("wall", 0), (only.What, only.Index));
        Assert.Contains("Door_Raise", only.Why);
        Assert.Equal(0, classic.Lines[0].Special);
        Assert.Equal(1, classic.Sectors[0].Special);
    }
}
