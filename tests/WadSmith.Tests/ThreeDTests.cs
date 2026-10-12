using SkiaSharp;
using WadSmith.App.ThreeD;
using WadSmith.Core.Games;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>The 3D view's picture (D46), drawn offscreen as the editor draws it.</summary>
public sealed class ThreeDTests
{
    private static readonly Lazy<GameResources> Freedoom = new(() =>
    {
        var wad = GameFiles.Require("freedoom2.wad");
        return GameResources.Load("freedoom2", null, GameLocator.Scan([Path.GetDirectoryName(wad)!]), GameFiles.Require("gzdoom.pk3"))!;
    });

    private static SKBitmap Render(Core.Maps.MapModel model, Camera3D camera, int size = 128)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The 3D view's windowless OpenGL comes to Windows with its own build (Phase 13).");
        var r = Freedoom.Value;
        var scene = MapScene.Build(MapGeometry.Build(EditorMap.FromModel(model).Frame), n => r.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(r));
        return SKBitmap.Decode(Offscreen3D.RenderPng(scene, r, camera, size, size));
    }

    private static (int R, int G, int B) Average(SKBitmap b, int x0, int y0, int x1, int y1)
    {
        long r = 0, g = 0, bl = 0, n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var c = b.GetPixel(x, y);
                (r, g, bl, n) = (r + c.Red, g + c.Green, bl + c.Blue, n + 1);
            }
        }

        return ((int)(r / n), (int)(g / n), (int)(bl / n));
    }

    [Fact]
    public void FromAboveTheStarterRoomShowsItsFloorAtItsBrightness()
    {
        var model = StarterRoom.Create("MAP01", "doom");
        using var picture = Render(model, Camera3D.Orbit(0, 0, 0, 90, -89.9, 300));

        // The floor texture's average colour, lit as the view lights an area of brightness 192 from above.
        var flat = Freedoom.Value.Flat("FLOOR4_8")!;
        long fr = 0, fg = 0, fb = 0;
        for (var i = 0; i < flat.Pixels.Length; i += 4) (fr, fg, fb) = (fr + flat.Pixels[i], fg + flat.Pixels[i + 1], fb + flat.Pixels[i + 2]);
        var count = flat.Pixels.Length / 4;
        var shade = 0.22 + 0.9 * 192 / 255.0;
        var (er, eg, eb) = ((int)(fr / count * shade), (int)(fg / count * shade), (int)(fb / count * shade));

        var (r, g, b) = Average(picture, 32, 32, 96, 96);
        static bool Close(int seen, int expected) => Math.Abs(seen - expected) <= Math.Max(3, expected * 0.08);
        Assert.True(Close(r, er) && Close(g, eg) && Close(b, eb), $"floor seen as {r},{g},{b}; expected about {er},{eg},{eb}");
    }

    [Fact]
    public void AWallWithATextureTheGameLacksShowsTheChecker()
    {
        var model = StarterRoom.Create("MAP01", "doom");
        foreach (var side in model.Sides) side.Middle = "NOSUCHTX";

        // From the middle of the room, facing a wall.
        using var picture = Render(model, new Camera3D(0, 0, 64, 90, 0));
        var magenta = 0;
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                var c = picture.GetPixel(x, y);
                if (c.Red > 120 && c.Blue > 100 && c.Green < 60) magenta++;
            }
        }

        Assert.True(magenta > picture.Width * picture.Height / 8, $"only {magenta} checker pixels");
    }

    public static TheoryData<string> GoldenSpots() => ["overview", "start"];

    /// <summary>
    /// Freedoom 2's MAP01 from fixed camera spots against stored pictures (Freedoom only: pictures of
    /// commercial games never enter the repository, D10). GPUs differ in edges and smoothing, so the
    /// pictures must agree on average and nearly everywhere rather than exactly. Set
    /// WADSMITH_WRITE_GOLDENS=1 to rewrite them after an intended change.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenSpots))]
    public void Freedoom2Map01LooksAsItDid(string spot)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The 3D view's windowless OpenGL comes to Windows with its own build (Phase 13).");
        var r = Freedoom.Value;
        var wad = Core.Containers.WadContainer.Open(GameFiles.Require("freedoom2.wad"));
        var lumps = Core.Maps.MapLocator.Find(wad).First(l => l.Name == "MAP01");
        var map = EditorMap.FromModel(Core.Maps.MapConversion.Read(lumps, "doom"));
        var scene = MapScene.Build(MapGeometry.Build(map.Frame), n => r.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, SpriteShapes.For(r));
        var start = scene.Things.First(t => t.Type == 1);
        var camera = spot == "overview" ? Offscreen3D.Overview(scene, 1.6) : new Camera3D(start.X, start.Y, start.Z + 41, start.Angle, 0);
        var png = Offscreen3D.RenderPng(scene, r, camera, 320, 200, inside: spot != "overview");
        var file = PreviewTests.Golden($"freedoom2-MAP01-3d-{spot}.png");
        if (Environment.GetEnvironmentVariable("WADSMITH_WRITE_GOLDENS") == "1")
        {
            File.WriteAllBytes(file, png);
            return;
        }

        using var drawn = SKBitmap.Decode(png);
        using var golden = SKBitmap.Decode(File.ReadAllBytes(file));
        Assert.Equal((golden.Width, golden.Height), (drawn.Width, drawn.Height));
        long total = 0;
        var far = 0;
        for (var y = 0; y < drawn.Height; y++)
        {
            for (var x = 0; x < drawn.Width; x++)
            {
                var a = drawn.GetPixel(x, y);
                var b = golden.GetPixel(x, y);
                var d = Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                total += d;
                if (d > 120) far++;
            }
        }

        var pixels = drawn.Width * drawn.Height;
        var mean = total / (3.0 * pixels);
        Assert.True(mean < 1.5 && far < pixels / 50, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{spot}: mean difference {mean:0.00} per channel, {far} of {pixels} pixels far off"));
    }
}
