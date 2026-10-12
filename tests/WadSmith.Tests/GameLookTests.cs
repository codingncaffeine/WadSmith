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
