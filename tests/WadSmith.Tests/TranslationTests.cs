using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Classic;
using WadSmith.Core.Translation;

namespace WadSmith.Tests;

/// <summary>The classic-to-GZDoom translation, using the engine's own translation tables (gzdoom.pk3).</summary>
public sealed class TranslationTests
{
    private static XlatTranslator Doom()
    {
        var pk3 = GameFiles.Require("gzdoom.pk3");
        return XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(pk3)]), "xlat/doom.txt");
    }

    private static int Action(string name) => ActionCatalog.Find(name)!.Number;

    /// <summary>A one-sector Doom-format map with one line of the given type and tag.</summary>
    private static MapModel DoomMap(ushort special, ushort tag = 0, ushort lineFlags = 0, ushort sectorSpecial = 0)
    {
        var c = new ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        c.Vertices.Add(new ClassicVertex(0, 0));
        c.Vertices.Add(new ClassicVertex(64, 0));
        c.Sectors.Add(new ClassicSector(0, 128, "FLOOR4_8", "CEIL3_5", 160, sectorSpecial, 0));
        c.Sides.Add(new ClassicSide(0, 0, "-", "-", "STARTAN3", 0));
        c.Lines.Add(new ClassicLine(0, 1, lineFlags, special, tag, 0, ClassicLine.NoSide));
        return MapConversion.FromClassic(c, "doom");
    }

    private static MapModel Translate(MapModel m, List<string>? notes = null) => MapTranslation.ToZDoom(m, Doom(), notes ?? []);

    [Fact]
    public void TheDoomTranslatorReadsTheEnginesTables()
    {
        var x = Doom();
        Assert.Equal(Action("Door_Raise"), x.Lines[1].Special);
        Assert.Equal(7, x.Boom.Count);
        Assert.NotEmpty(x.SectorMasks);
    }

    [Fact]
    public void AManualDoorBecomesDoorRaiseUsedByPlayersAndMonsters()
    {
        var l = Translate(DoomMap(1)).Lines[0];
        Assert.Equal(Action("Door_Raise"), l.Special);
        Assert.Equal([0, 16, 150, 0, 0], l.Args);
        Assert.True(l.Flag("playeruse") && l.Flag("monsteractivate") && l.Flag("repeatspecial"));
        Assert.False(l.Flag("playercross"));
    }

    [Fact]
    public void ABlueKeyDoorIsLockedWithTheBlueCardOrSkull()
    {
        var l = Translate(DoomMap(26)).Lines[0];
        Assert.Equal(Action("Door_LockedRaise"), l.Special);
        Assert.Equal([0, 16, 150, 2 | 128, 0], l.Args);
    }

    [Fact]
    public void ATaggedLineKeepsItsTagAsItsId()
    {
        var l = Translate(DoomMap(2, tag: 7)).Lines[0];
        Assert.Equal(Action("Door_Open"), l.Special);
        Assert.Equal(7, l.Args[0]);
        Assert.Equal(7, l.Id);
        Assert.True(l.Flag("playercross"));
    }

    [Fact]
    public void AShootableLineAlsoFiresOnProjectilesCrossing()
    {
        var l = Translate(DoomMap(24, tag: 3)).Lines[0];
        Assert.True(l.Flag("impact") && l.Flag("missilecross"));
    }

    [Fact]
    public void ABoomGeneralizedDoorIsTranslated()
    {
        // 0x3C00 range, trigger 3 (switch, repeatable), slow speed, open-wait-close, 8-tic delay bits zero.
        var l = Translate(DoomMap(0x3C03, tag: 5)).Lines[0];
        Assert.Equal(Action("Generic_Door"), l.Special);
        Assert.Equal(5, l.Args[0]);
        Assert.True(l.Flag("playeruse") && l.Flag("repeatspecial"));
    }

    [Fact]
    public void ABoomPushDoorOpensTheSectorBehindIt()
    {
        // Trigger 7 (push, repeatable): a manual door, told so in its third argument.
        var l = Translate(DoomMap(0x3C07)).Lines[0];
        Assert.Equal(Action("Generic_Door"), l.Special);
        Assert.Equal(0, l.Args[0]);
        Assert.Equal(128, l.Args[2] & 128);
    }

    [Theory]
    [InlineData(1, 65)]                       // blinking light: dLight_Flicker
    [InlineData(9, 1024)]                     // secret: GZDoom's secret bit
    [InlineData(7, 71)]                       // nukage damage
    [InlineData(0x80 | 7, 1024 | 71)]         // Boom: secret nukage
    [InlineData(0x20 | 0x80, 1024 | 0x100)]   // Boom: 5% damage and secret, no light type
    public void SectorTypesBecomeGzdoomTypes(int classic, int expected)
    {
        var s = Translate(DoomMap(0, sectorSpecial: (ushort)classic)).Sectors[0];
        Assert.Equal(expected, s.Special);
        Assert.False(s.Extra.Contains("secret"));
    }

    [Fact]
    public void AnUnknownLineTypeBecomesAPlainLineAndIsReported()
    {
        var notes = new List<string>();
        var l = Translate(DoomMap(999), notes).Lines[0];
        Assert.Equal(0, l.Special);
        Assert.Contains(notes, n => n.Contains("action 999", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBoomGarbageBitKeepsOnlyTheVanillaFlags()
    {
        // Bit 11 set: bits above 8 are an old editor's leftovers (here "pass use", bit 9).
        var l = Translate(DoomMap(0, lineFlags: 0x0800 | 0x0200 | 0x0001)).Lines[0];
        Assert.True(l.Flag("blocking"));
        Assert.False(l.Flag("passuse"));
    }

    [Fact]
    public void HexenLineIdentificationBecomesTheLinesId()
    {
        var c = new ClassicMap { Name = "MAP01", Format = MapFormat.Hexen };
        c.Lines.Add(new ClassicLine(0, 1, 0, (ushort)Action("Line_SetIdentification"), 0, 0, ClassicLine.NoSide, Arg0: 5, Arg4: 1));
        var l = MapTranslation.ToZDoom(MapConversion.FromClassic(c, "hexen"), null, []).Lines[0];
        Assert.Equal(261, l.Id);
        Assert.Equal(0, l.Special);
    }

    [Fact]
    public void HexenProjectileTouchIsImpactAndMissileCross()
    {
        var c = new ClassicMap { Name = "MAP01", Format = MapFormat.Hexen };
        c.Lines.Add(new ClassicLine(0, 1, 7 << 10, 12, 0, 0, ClassicLine.NoSide));
        var m = MapConversion.FromClassic(c, "hexen");
        Assert.True(m.Lines[0].Flag("impact") && m.Lines[0].Flag("missilecross"));
        // Unchanged, it writes back its own activation bits.
        Assert.Equal(7 << 10, MapConversion.ToClassic(m, MapFormat.Hexen).Lines[0].Flags & 0x1C00);
    }
}

public sealed class ChecksumTests
{
    [Fact]
    public void TheMapChecksumIsTheOneTheEngineUses()
    {
        // GZDoom's compatibility tables name this checksum as Doom II MAP14.
        var wad = WadContainer.Open(GameFiles.Require("DOOM2.WAD"));
        var map14 = MapLocator.Find(wad).First(m => m.Name == "MAP14");
        Assert.Equal("5BDA34DA60C0530794CC1EA2DA017976", MapChecksum.Compute(map14));
    }

    [Fact]
    public void TheEngineKnowsItsOwnFixesForThatMap()
    {
        var compat = EngineCompatibility.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]));
        Assert.True(compat.HasRepairs("5BDA34DA60C0530794CC1EA2DA017976"));
        Assert.True(compat.HasBehaviourSwitches("5BDA34DA60C0530794CC1EA2DA017976"));
        Assert.False(compat.HasRepairs("00000000000000000000000000000000"));
    }

    [Fact]
    public void StrifeTurnsOnClippedMiddleTexturesForEveryLine()
    {
        var engine = new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]);
        Assert.Equal(0x00080000, EngineCompatibility.GameDefaultLineFlags(engine, "strife"));
        Assert.Equal(0, EngineCompatibility.GameDefaultLineFlags(engine, "doom"));
    }
}

public sealed class TranslationDetailTests
{
    [Fact]
    public void EverySectorOfAClassicMapLetsThingsRideFallingFloors()
    {
        var c = new ClassicMap { Name = "MAP01", Format = MapFormat.Doom };
        c.Sectors.Add(new ClassicSector(0, 128, "F", "C", 160, 0, 0));
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var s = MapTranslation.ToZDoom(MapConversion.FromClassic(c, "doom"), xlat, []).Sectors[0];
        Assert.True(s.Extra.TryGet("dropactors", out var v) && v.AsBool());
    }
}
