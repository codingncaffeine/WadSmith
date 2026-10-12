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
using WadSmith.Editor.Smart;

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

/// <summary>"Explain this map" (D32): the report counts what the recognisers find, ties keys to their doors, calls out a lock with no key, and counts what each difficulty holds.</summary>
public sealed class MapReportTests
{
    private static EditorMap Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return map;
    }

    private static string Text(MapReport r) => r.Summary + "\n" + string.Join("\n", r.Sections.Select(s => s.Heading + ": " + string.Join(" | ", s.Lines.Select(l => l.Text + (l.At is null ? "" : " [show]")))));

    [Fact]
    public void AKeyWithItsDoorAndALockWithoutAKeyAreBothSaid()
    {
        var withKey = Text(MapReporting.Explain(Built("room 0 0 256 256\nroom 256 0 512 256\nstart 128 128\ndoor 256 128\nlock 256 128 red\nkey red 64 64").Frame, ProblemTests.Freedoom.Value));
        Assert.Contains("opens 1 door. [show]", withKey);
        Assert.DoesNotContain("the map has none", withKey);
        var without = Text(MapReporting.Explain(Built("room 0 0 256 256\nroom 256 0 512 256\nstart 128 128\ndoor 256 128\nlock 256 128 red").Frame, ProblemTests.Freedoom.Value));
        Assert.Contains("A door needs the red key, and the map has none: the player cannot open it. [show]", without);
        Assert.Contains("1 locked door. [show]", without);
    }

    [Fact]
    public void EachDifficultyCountsWhatIsOnIt()
    {
        var map = Built("room 0 0 512 256\nstart 128 128\nthing 3001 64 64\nthing 3001 96 64\nthing 2012 300 64\nthing 2001 300 128\nthing 2002 400 128");
        // A weapon for games with friends only: a player alone never meets it.
        new History(map).Record("Multiplayer only", e => e.Flag(map.Things.Single(t => t.Type == 2002), "single", false));
        new History(map).Record("Not on Easy", e => map.Things.Where(t => t.Type == 3001).Take(1).ToList().ForEach(t =>
        {
            e.Flag(t, "skill1", false);
            e.Flag(t, "skill2", false);
        }));
        var text = Text(MapReporting.Explain(map.Frame, ProblemTests.Freedoom.Value));
        Assert.Contains("Easy: 1 monster, 1 weapon, 0 ammunition, 1 health, 0 armour, 0 power-ups.", text);
        Assert.Contains("Normal: 2 monsters, 1 weapon, 0 ammunition, 1 health, 0 armour, 0 power-ups.", text);
        Assert.StartsWith("1 room, 2 monsters on Normal, 0 secrets and no exit.", text);
        Assert.Contains("Player 1 starts here. [show]", text);
    }

    [Fact]
    public void AGamesOwnMapIsCountedAsTheRecognisersFindIt()
    {
        var engine = GameFiles.Require("gzdoom.pk3");
        var wad = GameFiles.Require("freedoom2.wad");
        var map = Landmarks.Read(wad, "MAP01", engine, "doom")!;
        var report = MapReporting.Explain(map.Frame, ProblemTests.Freedoom.Value);
        var recognised = Recognition.Find(map, MapGeometry.Build(map.Frame));
        var text = Text(report);
        Assert.Contains($"{recognised.ByKind["secrets"].Count} secrets", report.Summary);
        Assert.Contains($"{map.Sectors.Count(s => s.OnMap)} rooms", report.Summary);
        Assert.Contains($"{recognised.ByKind["exits"].Count} exit", text);
    }
}

/// <summary>Explain this map in the window: the Inspector's button opens the card, Show centres the plan on its spot, Close shuts it.</summary>
[Collection(TimingCollection.Name)]
public sealed class ExplainThisMapInTheWindowTests
{
    [Fact]
    public async Task TheInspectorOpensTheCardAndShowCentresThePlan()
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
                editor.Session.Selection.Clear();
                Ui.Pump(0.3);
                editor.Inspector.Rows.OfType<App.ViewModels.ActionRow>().Single(r => r.Label == "Explain this map").Command.Execute(null);
                for (var i = 0; i < 100 && editor.Report is null; i++) Ui.Pump(0.02);
                said.Add(editor.Report?.Summary ?? "no card");
                var start = editor.Session.Map.Things.Single(t => t.Type == 1);
                bool Centred() => Math.Abs(plan.ToScreen(new(start.X, start.Y)).Y - plan.Bounds.Height / 2) < 4;
                plan.FitAround(new(start.X + 4000, start.Y + 4000), 640);
                Ui.Pump(0.3);
                said.Add($"moved away, centred {Centred()}");
                editor.Report!.Sections.Single(s => s.Heading == "WHERE IT STARTS").Items.Single().Show!.Execute(null);
                Ui.Pump(0.3);
                said.Add($"after Show, centred {Centred()}");
                editor.CloseReportCommand.Execute(null);
                said.Add(editor.Report is null ? "closed" : "open");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(["1 room, 0 monsters on Normal, 0 secrets and no exit.", "moved away, centred False", "after Show, centred True", "closed"], log);
    }
}
