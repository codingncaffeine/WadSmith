using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The guided first map's checks (§3.9): each step is finished by what it asks for and not by its near miss, the
/// planted fault each check must tell apart.
/// </summary>
public sealed class GuidedMapTests
{
    private static EditorMap Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return map;
    }

    private static GuideFacts Facts(EditorMap map, IReadOnlyDictionary<Side, (string, string, string)>? before = null, GameResources? resources = null) =>
        GuidedMap.Facts(map.Frame, resources, before ?? new Dictionary<Side, (string, string, string)>());

    private static GuideStep Step(string id) => GuidedMap.Steps.Single(s => s.Id == id);

    private const string TwoRooms = "room 0 0 256 256\nroom 256 0 512 256\nstart 128 128";

    [Fact]
    public void ARoomDrawnFinishesTheFirstStepAndTheSameRoomsDoNot()
    {
        var one = Facts(Built("room 0 0 256 256\nstart 128 128"));
        var two = Facts(Built(TwoRooms));
        Assert.True(GuidedMap.Done(Step("room"), two, one, false, false));
        Assert.False(GuidedMap.Done(Step("room"), one, one, false, false));
    }

    [Fact]
    public void ADoorFinishesTheDoorStepAndTwoRoomsSharingAWallDoNot()
    {
        var start = Facts(Built(TwoRooms));
        Assert.False(GuidedMap.Done(Step("door"), start, start, false, false));
        var door = Facts(Built(TwoRooms + "\ndoor 256 128"));
        Assert.True(GuidedMap.Done(Step("door"), door, start, false, false));
    }

    [Fact]
    public void AWallRetexturedFinishesTheTextureStepAndAFloorDoesNot()
    {
        var map = Built(TwoRooms);
        var before = GuidedMap.Walls(map.Frame);
        var start = Facts(map, before);
        var history = new History(map);
        history.Run(new SetProperty<Sector, string>(map.Sectors[0], "Floor texture", s => s.FloorTexture, (s, v) => s.FloorTexture = v, "FLAT5_4"));
        Assert.False(GuidedMap.Done(Step("texture"), Facts(map, before), start, false, false));
        var side = map.Lines.First(l => l.OnMap).Front;
        history.Run(new SetProperty<Side, string>(side, "Wall texture", s => s.Middle, (s, v) => s.Middle = v, "BROWN1"));
        Assert.Equal(1, Facts(map, before).WallsRetextured);
        Assert.True(GuidedMap.Done(Step("texture"), Facts(map, before), start, false, false));
    }

    [Fact]
    public void AMonsterAndAWeaponFinishTheThingsStepAndAMonsterAloneDoesNot()
    {
        var freedoom = ProblemTests.Freedoom.Value;
        var start = Facts(Built(TwoRooms), resources: freedoom);
        var monster = Facts(Built(TwoRooms + "\nthing 3001 64 64"), resources: freedoom);
        Assert.Equal(1, monster.Monsters);
        Assert.False(GuidedMap.Done(Step("things"), monster, start, false, false));
        var both = Facts(Built(TwoRooms + "\nthing 3001 64 64\nthing 2001 300 64"), resources: freedoom);
        Assert.Equal((1, 1), (both.Monsters, both.Weapons));
        Assert.True(GuidedMap.Done(Step("things"), both, start, false, false));
    }

    [Fact]
    public void AnExitFinishesTheExitStepAndADoorDoesNot()
    {
        var door = Facts(Built(TwoRooms + "\ndoor 256 128"));
        Assert.False(GuidedMap.Done(Step("exit"), door, door, false, false));
        var exit = Facts(Built(TwoRooms + "\nexit 512 128"));
        Assert.True(GuidedMap.Done(Step("exit"), exit, door, false, false));
    }

    [Fact]
    public void LookingIn3DAndPlayingFinishTheirStepsAndNothingElseDoes()
    {
        var facts = Facts(Built(TwoRooms + "\ndoor 256 128\nexit 512 128"));
        Assert.False(GuidedMap.Done(Step("look"), facts, facts, saw3D: false, played: true));
        Assert.True(GuidedMap.Done(Step("look"), facts, facts, saw3D: true, played: false));
        Assert.False(GuidedMap.Done(Step("play"), facts, facts, saw3D: true, played: false));
        Assert.True(GuidedMap.Done(Step("play"), facts, facts, saw3D: false, played: true));
    }

    [Fact]
    public void EveryStepNamesAControlAndTheGamesOwnThings()
    {
        Assert.Equal(["room", "look", "door", "texture", "things", "exit", "play"], GuidedMap.Steps.Select(s => s.Id));
        Assert.DoesNotContain(GuidedMap.Steps, s => s.Target == GuideTarget.None);
        Assert.Equal(("a Gargoyle", "the Ethereal Crossbow"), GuidedMap.Examples("heretic"));
        Assert.Equal(("an Imp", "a shotgun"), GuidedMap.Examples("doom"));
    }
}

/// <summary>
/// The guided first map in the real window, started from the start screen's tile: each step done the way the editor's
/// tools do it moves the card on, the outline follows the step, and nothing moves it on by itself.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class GuidedMapInTheWindowTests
{
    [Fact]
    public async Task SevenStepsFromTheStartScreenToPlay()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var said = new List<string>();
            try
            {
                var tile = vm.Welcome.Games.First();
                Ui.Wait(tile.GuidedMap!.ExecuteAsync(tile));
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                var guide = editor.Guide!;
                var session = editor.Session;
                string Now() => $"{guide.StepLabel}: {guide.Title}{(editor.Marks.Room ? " [room]" : "")}{(editor.Marks.Door ? " [door]" : "")}";
                // Waits until the step moves on (the check runs on a worker), or two seconds.
                void Settle()
                {
                    var at = guide.Index;
                    for (var i = 0; i < 100 && guide.Index == at && !guide.Finished; i++) Ui.Pump(0.02);
                    Ui.Pump(0.05);
                }

                Ui.Pump(0.5);
                said.Add(Now());
                var xs = session.Map.Vertices.Where(v => v.OnMap).Select(v => v.X).ToList();
                var ys = session.Map.Vertices.Where(v => v.OnMap).Select(v => v.Y).ToList();
                var (x1, y0, y1) = (xs.Max(), ys.Min(), ys.Max());
                session.DrawRoom([new(x1, y0), new(x1 + 256, y0), new(x1 + 256, y1), new(x1, y1)]);
                Settle();
                said.Add(Now());

                var canvas = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<App.Canvas.MapCanvas>().First();
                canvas.Focus();
                Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Tab, null);
                Settle();
                editor.Layout = MapLayout.Split;
                said.Add(Now());

                session.MakeDoor(session.Map.Lines.First(l => l.OnMap && l.Back is not null));
                Settle();
                said.Add(Now());

                session.Paint("BROWN1", new((xs.Min() + x1) / 2, y0), 8, ceiling: false);
                Settle();
                said.Add(Now());

                session.PlaceThing(3001, new(x1 + 128, (y0 + y1) / 2 + 32));
                Ui.Pump(0.3);
                var stillThings = guide.Title;
                session.PlaceThing(2001, new(x1 + 128, (y0 + y1) / 2 - 32));
                Settle();
                said.Add($"{stillThings} → {Now()}");

                session.Selection.Set(session.Map.Lines.First(l => l.OnMap && l.Back is null && l.Start.X == x1 + 256 && l.End.X == x1 + 256));
                Ui.Pump(0.2);
                editor.Inspector.Rows.OfType<App.ViewModels.ActionRow>().Single(r => r.Label == "Make it the exit switch").Command.Execute(null);
                Settle();
                said.Add(Now());

                guide.Played();
                Settle();
                said.Add($"{guide.StepLabel}: {guide.Title} {guide.Finished}");
                guide.StopCommand.Execute(null);
                said.Add(editor.Guide is null ? "stopped" : "still there");
            }
            finally
            {
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "Step 1 of 7: Draw a room [room]",
            "Step 2 of 7: See it in 3D",
            "Step 3 of 7: Put a door between them [door]",
            "Step 4 of 7: Give a wall a new look",
            "Step 5 of 7: Add a monster and a weapon",
            "Add a monster and a weapon → Step 6 of 7: Add a way out",
            "Step 7 of 7: Play it",
            "Your first map: You made a Doom map. True",
            "stopped",
        ], log);
    }
}
