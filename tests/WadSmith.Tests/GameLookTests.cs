using WadSmith.App.Canvas;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The map drawn with its game's own pictures (D45): floors, ceilings, sprites and names.</summary>
public sealed class GameLookTests
{
    private static readonly Lazy<GameResources> Freedoom = new(() =>
    {
        var wad = GameFiles.Require("freedoom2.wad");
        var games = GameLocator.Scan([Path.GetDirectoryName(wad)!]);
        return GameResources.Load("freedoom2", null, games, GameFiles.Require("gzdoom.pk3")) ?? throw new InvalidOperationException("freedoom2 was not recognised");
    });

    [Fact]
    public void TheGameGivesFloorsSpritesAndNames()
    {
        var r = Freedoom.Value;
        var flat = r.Flat("FLOOR4_8");
        Assert.NotNull(flat);
        Assert.Equal(64, flat.Width);
        Assert.Null(r.Flat("-"));
        Assert.Null(r.Flat("NOSUCHFL"));

        var imp = r.SpriteFor(3001);
        Assert.NotNull(imp);
        Assert.True(imp.Width > 8 && imp.Height > 8);
        Assert.Equal("Imp", r.Things.Find(3001)?.Name);
        Assert.Null(r.SpriteFor(14));
    }

    private static MapGeometry Map01()
    {
        var wad = WadContainer.Open(GameFiles.Require("freedoom2.wad"));
        var lumps = MapLocator.Find(wad).First(l => l.Name == "MAP01");
        var map = EditorMap.FromModel(MapConversion.Read(lumps, "doom"));
        return MapGeometry.Build(map.Frame);
    }

    [Fact]
    public void FloorsAreGroupedByTextureAndBrightnessAndPictured()
    {
        var geometry = Map01();
        var picture = CanvasSnapshot.Take(geometry, new MapLook(Freedoom.Value), MapViewMode.Floors);
        var f = geometry.Frame;
        var pairs = f.Sectors.Where(s => s.Element is not null && geometry.LoopsAt(s.Element.Slot).Count > 0).Select(s => (s.FloorTexture, Math.Clamp(s.Light, 0, 255))).Distinct().Count();

        Assert.NotNull(picture.Areas);
        Assert.Equal(pairs, picture.AreaStyles.Length);
        Assert.All(picture.AreaStyles, s => Assert.NotNull(s.Flat));
        Assert.Contains(picture.Things, t => t.Sprite is not null);
    }

    [Fact]
    public void LinesShowNoAreasAndHeightsShowOneColourPerHeight()
    {
        var geometry = Map01();
        var look = new MapLook(Freedoom.Value);
        Assert.Null(CanvasSnapshot.Take(geometry, look, MapViewMode.Lines).Areas);

        var heights = CanvasSnapshot.Take(geometry, look, MapViewMode.Heights);
        var distinct = geometry.Frame.Sectors.Where(s => s.Element is not null && geometry.LoopsAt(s.Element.Slot).Count > 0).Select(s => s.FloorHeight).Distinct().Count();
        Assert.Equal(Math.Min(distinct, 64), heights.AreaStyles.Length);
        Assert.All(heights.AreaStyles, s => Assert.Null(s.Flat));
    }

    [Fact]
    public void BrightnessShowsOneGreyPerLightLevelAsBrightAsIt()
    {
        var geometry = Map01();
        var lights = geometry.Frame.Sectors.Where(s => s.Element is not null && geometry.LoopsAt(s.Element.Slot).Count > 0).Select(s => Math.Clamp(s.Light, 0, 255)).Distinct().Order().ToList();
        var picture = CanvasSnapshot.Take(geometry, new MapLook(Freedoom.Value), MapViewMode.Brightness);
        Assert.True(lights.Count > 1);
        Assert.Equal(lights, picture.AreaStyles.Select(s => (int)s.Solid.Red).Order());
        Assert.All(picture.AreaStyles, s => Assert.True(s.Flat is null && s.Solid.Red == s.Solid.Green && s.Solid.Green == s.Solid.Blue));
    }

    [Fact]
    public void ShrunkCopiesHalveEachTime()
    {
        var flat = new MapLook(Freedoom.Value).Flat("FLOOR4_8");
        Assert.NotNull(flat);
        Assert.Equal(64, flat.Level(0).Width);
        Assert.Equal(32, flat.Level(1).Width);
        Assert.Equal(4, flat.Level(4).Width);
        Assert.Equal(4, flat.Level(9).Width);
    }
}

/// <summary>Phase 2's proof that the editor opens every map of every base game here.</summary>
public sealed class EveryMapOpensTests
{
    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryMapBuildsItsShapesAndPicture(string fileName, string game)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var look = new MapLook(null);
        foreach (var lumps in MapLocator.Find(wad))
        {
            var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
            var geometry = MapGeometry.Build(map.Frame);
            var picture = CanvasSnapshot.Take(geometry, look, MapViewMode.Floors);
            // Every closed area gets an outline. Closed, decided apart from the tracer: leaving out walls
            // with the area on both sides (a deliberate trick), at least three walls remain and each
            // corner has as many of them arriving as leaving. Unclosed areas (control areas and other
            // tricks in the original maps) are left to the checks.
            var untraced = map.Sectors.Where(s => Closed(map, s) && geometry.Loops(s).Count == 0).Select(s => $"#{s.Slot}").ToList();
            Assert.True(untraced.Count == 0, $"{fileName} {lumps.Name}: no outline for closed areas " + string.Join(", ", untraced));
            Assert.Equal(map.Lines.Count, picture.Walls.Counts.Sum());
        }
    }

    private static bool Closed(EditorMap map, Sector s)
    {
        var edges = new List<(Vertex From, Vertex To)>();
        foreach (var l in map.Lines)
        {
            if (l.Front.Sector == s && l.Back?.Sector == s) continue;
            if (l.Front.Sector == s) edges.Add((l.Start, l.End));
            if (l.Back?.Sector == s) edges.Add((l.End, l.Start));
        }

        if (edges.Count < 3) return false;
        var balance = new Dictionary<Vertex, int>();
        foreach (var (from, to) in edges)
        {
            balance[from] = balance.GetValueOrDefault(from) + 1;
            balance[to] = balance.GetValueOrDefault(to) - 1;
        }

        return balance.Values.All(v => v == 0);
    }
}
