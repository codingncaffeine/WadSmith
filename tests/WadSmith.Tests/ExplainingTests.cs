using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// "What is this?" (D33): each kind of thing in a map is named as the editor recognises it, and the sentence says what
/// a player meets there — the steps they climb and the ones they cannot, a room too low, the difficulties a monster is on.
/// </summary>
public sealed class ExplainingTests
{
    private static (EditorMap Map, MapGeometry Geometry) Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return (map, MapGeometry.Build(map.Frame));
    }

    private static Explanation Of(Element e, MapGeometry g) => Explaining.Explain(e, g, ProblemTests.Freedoom.Value)!;

    private static Sector At(MapGeometry g, double x, double y) => g.SectorAt(new MapPoint(x, y))!;

    private static Line WallAt(MapGeometry g, double x, double y) => g.LineNear(new MapPoint(x, y), 4)!;

    private const string TwoRooms = "room 0 0 256 256\nroom 256 0 512 256\nstart 128 128";

    [Fact]
    public void ARoomSaysItsHeightsAndWhenThePlayerCannotFit()
    {
        var (_, g) = Built(TwoRooms + "\nceiling 384 128 40");
        var room = Of(At(g, 128, 128), g);
        Assert.Equal("Room", room.Heading);
        Assert.Contains("floor at 0, ceiling at 128", room.Text);
        Assert.DoesNotContain("too low", room.Text);
        Assert.Contains("too low for the player", Of(At(g, 384, 128), g).Text);
    }

    [Fact]
    public void AnOpeningSaysWhetherThePlayerWalksClimbsOrIsStopped()
    {
        Assert.Contains("walks straight through", Of(WallAt(Built(TwoRooms).Geometry, 256, 128), Built(TwoRooms).Geometry).Text);
        var (_, step) = Built(TwoRooms + "\nfloor 384 128 16");
        Assert.Contains("a step the player climbs", Of(WallAt(step, 256, 128), step).Text);
        var (_, ledge) = Built(TwoRooms + "\nfloor 384 128 64");
        Assert.Contains("more than the player climbs", Of(WallAt(ledge, 256, 128), ledge).Text);
        var (_, low) = Built(TwoRooms + "\nceiling 384 128 40");
        Assert.Contains("too low for the player", Of(WallAt(low, 256, 128), low).Text);
        var (_, solid) = Built(TwoRooms);
        Assert.Equal("Solid wall", Of(WallAt(solid, 0, 128), solid).Heading);
    }

    [Fact]
    public void SmartObjectsAreNamedAsTheEditorRecognisesThem()
    {
        var (map, g) = Built(TwoRooms + "\nroom 512 96 576 160\ndoor 256 128\nlock 256 128 red\nkey red 64 64\nfloor 384 200 64\nexit 0 200\nsecret 544 128\nlight 100 100");
        var door = Of(At(g, 256, 128), g);
        Assert.Equal("Door", door.Heading);
        Assert.Contains("with the red key", door.Text);
        // The door's own face says what the door does.
        var face = map.Lines.First(l => l.OnMap && l.Special == Editor.Smart.Doors.DoorLockedRaise);
        Assert.Equal("Door", Of(face, g).Heading);
        Assert.Equal("Door", Of(face.Front, g).Heading);
        Assert.Equal("Secret area", Of(At(g, 544, 128), g).Heading);
        Assert.Equal("Exit switch", Of(WallAt(g, 0, 200), g).Heading);
        Assert.Contains("open doors locked with the red key", Of(map.Things.Single(t => t.Type == 13), g).Text);
        Assert.Equal("Player 1 start", Of(map.Things.Single(t => t.Type == 1), g).Heading);
        Assert.Equal("Light", Of(map.Things.Single(t => Editor.Smart.Lights.Kind(t.Type) is not null), g).Heading);
        Assert.Equal("Corner", Of(map.Vertices.First(v => v.OnMap), g).Heading);
    }

    [Fact]
    public void LiftsStairsAndSwitchesAreNamed()
    {
        var (_, lift) = Built(TwoRooms + "\nfloor 384 128 64\nlift 384 128");
        Assert.Equal("Lift", Of(At(lift, 400, 128), lift).Heading);
        var (_, g) = Built(TwoRooms + "\nstairs 0 64 192 192\ndoor 256 64\nswitch 256 64 0 32");
        Assert.Equal("Stairs", Of(At(g, 100, 128), g).Heading);
        Assert.Equal("Switch", Of(WallAt(g, 0, 32), g).Heading);
    }

    [Fact]
    public void AMonsterSaysWhichDifficultiesItIsOnOnlyWhenNotAll()
    {
        var (map, g) = Built(TwoRooms + "\nthing 3001 64 64");
        var imp = map.Things.Single(t => t.Type == 3001);
        var all = Of(imp, g);
        Assert.Equal(ProblemTests.Freedoom.Value.Things.Find(3001)!.Name, all.Heading);
        Assert.StartsWith("A monster.", all.Text);
        Assert.DoesNotContain("appears", all.Text);
        new History(map).Record("Appears on Easy", e =>
        {
            e.Flag(imp, "skill1", false);
            e.Flag(imp, "skill2", false);
        });
        Assert.Contains("It appears on Normal and Hard only.", Of(imp, g).Text);
    }

    [Fact]
    public void NothingIsExplainedAsNothing() => Assert.Null(Explaining.Explain(null, Built(TwoRooms).Geometry, null));
}

/// <summary>
/// "What is this?" in the real window: Shift+F1 turns it on and off, and a real pointer over the plan or the 3D view
/// fills the card with what is there.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class WhatIsThisInTheWindowTests
{
    [Fact]
    public async Task ShiftF1AndThePointerFillTheCardFromBothViews()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                var plan = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                var view3d = window.GetVisualDescendants().OfType<App.ThreeD.View3D>().First();
                string Card() => editor.Explained is { } e ? $"{e.Heading}" : "no card";
                void Over(Visual v, double fx, double fy)
                {
                    var p = v.TranslatePoint(new Point(v.Bounds.Width * fx, v.Bounds.Height * fy), window)!.Value;
                    window.MouseMove(p, RawInputModifiers.None);
                    for (var i = 0; i < 50; i++) Ui.Pump(0.02);
                }

                Ui.Pump(1.0);
                Over(plan, 0.5, 0.5);
                said.Add(Card());
                Over(plan, 0.98, 0.98);
                plan.Focus();
                window.KeyPress(Key.F1, RawInputModifiers.Shift, PhysicalKey.F1, null);
                Ui.Pump(0.2);
                said.Add(Card());
                Over(plan, 0.5, 0.5);
                said.Add(Card());
                Over(plan, 0.98, 0.98);
                said.Add(Card());
                Over(view3d, 0.5, 0.6);
                said.Add(Card());
                window.KeyPress(Key.F1, RawInputModifiers.Shift, PhysicalKey.F1, null);
                Ui.Pump(0.2);
                said.Add(Card());
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["no card", "Point at something", "Room", "Point at something", "Room", "no card"], log);
    }
}
