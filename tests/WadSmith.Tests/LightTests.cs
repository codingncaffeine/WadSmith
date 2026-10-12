using SkiaSharp;
using WadSmith.App.ThreeD;
using WadSmith.Core.Games;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Lights: GZDoom's point lights made, recognised, and shown in the 3D view.</summary>
public sealed class LightTests
{
    private static readonly Lazy<GameResources> Freedoom = new(() =>
    {
        var wad = GameFiles.Require("freedoom2.wad");
        return GameResources.Load("freedoom2", null, GameLocator.Scan([Path.GetDirectoryName(wad)!]), GameFiles.Require("gzdoom.pk3"))!;
    });

    [Fact]
    public void ANewLightIsSoftWarmAndEverywhere()
    {
        var light = Lights.New(new MapPoint(64, 32), "doom");
        Assert.Equal(9830, light.Type);
        Assert.Equal([255, 224, 192, 128, 64], light.Args);
        Assert.Equal(48, light.Height);
        Assert.All(new[] { "skill1", "skill5", "single", "coop", "dm" }, f => Assert.True(light.Flag(f)));
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        new Editor.Editing.History(map).Record("Light", e => e.Add(light));
        var seen = Lights.Recognise(light)!;
        Assert.Equal((LightKind.Steady, true, (255, 224, 192), 128), (seen.Kind, seen.Soft, seen.Colour, seen.Reach));
    }

    [Fact]
    public void EveryPointLightIsKnownAndNothingElse()
    {
        foreach (var kind in Enum.GetValues<LightKind>())
        {
            foreach (var soft in new[] { false, true })
            {
                var type = Lights.TypeOf(kind, soft);
                Assert.Equal(kind, Lights.Kind(type));
            }
        }

        Assert.Equal(LightKind.Pulsing, Lights.Kind(9811));
        Assert.True(Lights.Subtracts(9820));
        Assert.Null(Lights.Kind(9803));
        Assert.Null(Lights.Kind(9840));
        Assert.Null(Lights.Kind(3004));
        Assert.Equal("#FFE0C0", Lights.Hex((255, 224, 192)));
        Assert.Equal((18, 52, 86), Lights.ParseHex(" #123456 "));
        Assert.Null(Lights.ParseHex("#12345"));
    }

    [Fact]
    public void TheSceneHangsEachLightAboveItsFloorAndTheViewShowsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The 3D view's windowless OpenGL comes to Windows with its own build (Phase 13).");
        var model = StarterRoom.Create("MAP01", "doom");
        model.Sectors[0].Light = 64;
        model.Sectors[0].FloorHeight = 16;
        var r = Freedoom.Value;
        SKBitmap Picture(EditorMap map)
        {
            var scene = MapScene.Build(MapGeometry.Build(map.Frame), n => r.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(r));
            return SKBitmap.Decode(Offscreen3D.RenderPng(scene, r, Camera3D.Orbit(0, 0, 0, 90, -89.9, 300), 128, 128));
        }

        var dark = EditorMap.FromModel(model);
        using var without = Picture(dark);

        var lit = EditorMap.FromModel(model);
        var light = Lights.New(new MapPoint(0, 0), "doom");
        (light.Args[0], light.Args[1], light.Args[2], light.Args[3]) = (255, 0, 0, 256);
        new Editor.Editing.History(lit).Record("Light", e => e.Add(light));
        var scene = MapScene.Build(MapGeometry.Build(lit.Frame), _ => null);
        var hung = Assert.Single(scene.Lights);
        Assert.Equal((0f, 0f, 64f, 1f, 0f, 0f, 512f, true), (hung.X, hung.Y, hung.Z, hung.R, hung.G, hung.B, hung.Reach, hung.Soft));
        using var with = Picture(lit);

        // Under the light the floor turns red; its green and blue stay as they were.
        static (int R, int G, int B) Middle(SKBitmap b)
        {
            long rr = 0, gg = 0, bb = 0;
            for (var y = 56; y < 72; y++)
            {
                for (var x = 56; x < 72; x++)
                {
                    var c = b.GetPixel(x, y);
                    (rr, gg, bb) = (rr + c.Red, gg + c.Green, bb + c.Blue);
                }
            }

            return ((int)(rr / 256), (int)(gg / 256), (int)(bb / 256));
        }

        var (r0, g0, b0) = Middle(without);
        var (r1, g1, b1) = Middle(with);
        Assert.True(r1 > r0 + 30, $"red {r0} → {r1}");
        Assert.True(Math.Abs(g1 - g0) <= 3 && Math.Abs(b1 - b0) <= 3, $"green {g0} → {g1}, blue {b0} → {b1}");
    }

    /// <summary>
    /// GZDoom, measured 2026-10-12 (tools/engine/view-shot.sh from 641 above a dark floor, a 256-wide bright square for
    /// the scale, 1.8 units a pixel): a light of size 256, 48 above the floor, fades to nothing 513 units out, twice its
    /// size; a plain one in a straight line with distance, a soft (attenuated) one also by the way the floor faces it
    /// (red every 27 units: plain 85 89 79 83 71 65 59 60 53 47 45 …, soft 85 77 52 43 30 23 18 17 13 11 10 …), which
    /// (1 − d / 512) · cos(angle to the light) fits within a few counts. The 3D view must light alike: measured
    /// against the same room unlit and fully bright, so the floor's pattern cancels out.
    /// </summary>
    [Fact]
    public void TheViewLightsAsFarAndAsSoftlyAsGZDoom()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The 3D view's windowless OpenGL comes to Windows with its own build (Phase 13).");
        var r = Freedoom.Value;
        SKBitmap Picture(int? lightType, int brightness)
        {
            var map = EditorMap.FromModel(new Core.Maps.MapModel { Name = "MAP01", Namespace = "zdoom" });
            var history = new Editor.Editing.History(map);
            history.Record("Room", e => Editor.Editing.ShapeDrawing.Draw(e, MapGeometry.Build(map.Frame), [new(-1024, -1024), new(-1024, 1024), new(1024, 1024), new(1024, -1024)], Look.For("doom")));
            var room = map.Sectors.Single(x => x.OnMap);
            history.Record("Room", e =>
            {
                e.Set(room, x => x.Light, (x, v) => x.Light = v, brightness);
                e.Set(room, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, 1024.0);
            });
            if (lightType is { } type)
            {
                var light = Lights.New(new MapPoint(0, 0), "doom");
                light.Type = type;
                (light.Args[0], light.Args[1], light.Args[2], light.Args[3]) = (255, 0, 0, 256);
                history.Record("Light", e => e.Add(light));
            }

            var scene = MapScene.Build(MapGeometry.Build(map.Frame), n => r.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(r));
            return SKBitmap.Decode(Offscreen3D.RenderPng(scene, r, new Camera3D(0, 0, 641, 90, -89.9, Stretch: 1), 512, 320));
        }

        using var dark = Picture(null, 0);
        using var bright = Picture(null, 255);
        using var plain = Picture(9800, 0);
        using var soft = Picture(9830, 0);

        // Units a pixel from the camera: 641 above the floor, its field of view across (drawn without the game's pixel stretch, so a
        // pixel is the same number of units across and along).
        var unitsPerPixel = 2 * 641 * Math.Tan(74 * Math.PI / 360) * 512 / 320 / 512;

        // The light's share at a distance beside it: how much of the way from unlit (0.22 shade) to full (1.0) it adds.
        double Fall(SKBitmap lit, double units)
        {
            var x = 256 + (int)Math.Round(units / unitsPerPixel);
            double Sum(SKBitmap b) => Enumerable.Range(-6, 13).Sum(dy => Enumerable.Range(-2, 5).Sum(dx => (double)b.GetPixel(x + dx, 160 + dy).Red));
            return (Sum(lit) - Sum(dark)) / (Sum(bright) - Sum(dark)) * (1.0 - 0.22);
        }

        static double Model(double h, bool soft)
        {
            var d = Math.Sqrt(h * h + 48 * 48);
            return Math.Max(0, 1 - d / 512) * (soft ? 48 / d : 1);
        }

        foreach (var h in new[] { 0.0, 54, 108, 216, 324 })
        {
            Assert.True(Math.Abs(Fall(plain, h) - Model(h, false)) <= 0.08, $"plain {h}: {Fall(plain, h):0.00}, GZDoom's {Model(h, false):0.00}");
            Assert.True(Math.Abs(Fall(soft, h) - Model(h, true)) <= 0.08, $"soft {h}: {Fall(soft, h):0.00}, GZDoom's {Model(h, true):0.00}");
        }

        Assert.True(Fall(plain, 560) < 0.03 && Fall(soft, 560) < 0.03, $"beyond the reach: plain {Fall(plain, 560):0.00}, soft {Fall(soft, 560):0.00}");
    }
}
