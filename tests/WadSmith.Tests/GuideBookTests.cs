using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// The Guide's checks (§3.9): every step of every chapter is finished by what it asks for and not by its near miss, the
/// planted fault each check must tell apart.
/// </summary>
public sealed class GuideBookTests
{
    private static EditorMap Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Then(map, script);
        return map;
    }

    private static MapFrame Then(EditorMap map, string script)
    {
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return map.Frame;
    }

    private static GuideStep Step(string chapter, string id) => GuideBook.Chapters.Single(c => c.Id == chapter).Steps.Single(s => s.Id == id);

    private static bool Done(string chapter, string id, MapFrame now, MapFrame start, GameResources? resources = null, params string[] happened) =>
        GuideBook.Done(Step(chapter, id), now, start, resources, new HashSet<string>(happened));

    private const string TwoRooms = "room 0 0 256 256\nroom 256 0 512 256\nstart 128 128";

    private static void Set<T, TV>(EditorMap map, T element, Func<T, TV> get, Action<T, TV> set, TV value) where T : Element =>
        new History(map).Run(new SetProperty<T, TV>(element, "Change", get, set, value));

    [Fact]
    public void ARoomDrawnFinishesTheRoomStepAndTheSameRoomsDoNot()
    {
        var one = Built("room 0 0 256 256\nstart 128 128").Frame;
        var two = Built(TwoRooms).Frame;
        Assert.True(Done("first", "room", two, one));
        Assert.False(Done("first", "room", one, one));
        Assert.True(Done("rooms", "room", two, one));
    }

    [Fact]
    public void ANewDoorFinishesTheDoorStepAndTwoRoomsSharingAWallOrADoorAlreadyThereDoNot()
    {
        var map = Built(TwoRooms);
        var start = map.Frame;
        Assert.False(Done("first", "door", start, start));
        var door = Then(map, "door 256 128");
        Assert.True(Done("first", "door", door, start));
        Assert.True(Done("doors", "door", door, start));
        // A remix's own doors are not the user's: the step waits for one more.
        Assert.False(Done("doors", "door", door, door));
    }

    [Fact]
    public void AWallRetexturedFinishesTheTextureStepAndAFloorDoesNot()
    {
        var map = Built(TwoRooms);
        var start = map.Frame;
        Set(map, map.Sectors[0], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "FLAT5_4");
        Assert.False(Done("first", "texture", map.Frame, start));
        Assert.True(Done("textures", "flat", map.Frame, start));
        Set(map, map.Lines.First(l => l.OnMap).Front, s => s.Middle, (s, v) => s.Middle = v, "BROWN1");
        Assert.True(Done("first", "texture", map.Frame, start));
        Assert.True(Done("textures", "texture", map.Frame, start));
    }

    [Fact]
    public void AMonsterAndAWeaponFinishTheThingsStepAndAMonsterAloneDoesNot()
    {
        var freedoom = ProblemTests.Freedoom.Value;
        var map = Built(TwoRooms);
        var start = map.Frame;
        var monster = Then(map, "thing 3001 64 64");
        Assert.False(Done("first", "things", monster, start, freedoom));
        Assert.True(Done("things", "monster", monster, start, freedoom));
        Assert.False(Done("things", "pickup", monster, start, freedoom));
        var both = Then(map, "thing 2001 300 64");
        Assert.True(Done("first", "things", both, start, freedoom));
        Assert.False(Done("things", "pickup", both, start, freedoom));
        var health = Then(map, "thing 2012 300 128");
        Assert.True(Done("things", "pickup", health, start, freedoom));
    }

    [Fact]
    public void ChoosingADoorsLookFinishesTheLookStep()
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        var withDoor = Then(map, TwoRooms + "\ndoor 256 128");
        var history = new History(map);
        var door = map.Sectors.Single(s => s.OnMap && Doors.Recognise(MapGeometry.Build(map.Frame), s) is not null);
        var found = Doors.Recognise(MapGeometry.Build(map.Frame), door)!;
        Assert.False(Done("doors", "doorlook", map.Frame, withDoor));
        // The script's door on a wide opening already shows the big door: a short one is a change.
        history.Record("Change the door's look", e => Doors.Dress(e, MapGeometry.Build(map.Frame), found, "DOOR1", (64, 72)));
        Assert.True(Done("doors", "doorlook", map.Frame, withDoor));
    }

    [Fact]
    public void AnExitFinishesTheExitStepAndADoorDoesNot()
    {
        var map = Built(TwoRooms);
        var door = Then(map, "door 256 128");
        Assert.False(Done("first", "exit", door, door));
        var exit = Then(map, "exit 512 128");
        Assert.True(Done("first", "exit", exit, door));
    }

    [Fact]
    public void EventStepsFinishOnTheirOwnEventAndNoOther()
    {
        var f = Built(TwoRooms).Frame;
        (string Chapter, string Step, string Event)[] steps =
        [
            ("first", "look", GuideEvents.ThreeD), ("first", "play", GuideEvents.Played), ("rooms", "heights", GuideEvents.Heights),
            ("lifts", "walk", GuideEvents.Walk), ("things", "difficulty", GuideEvents.Difficulty), ("settings", "name", GuideEvents.MapName),
            ("settings", "music", GuideEvents.MapMusic), ("settings", "play", GuideEvents.Played), ("veterans", "names", GuideEvents.ClassicNames),
            ("veterans", "keys", GuideEvents.BuilderKeys), ("veterans", "palette", GuideEvents.Palette),
        ];
        var all = steps.Select(s => s.Event).Distinct().ToArray();
        foreach (var (chapter, step, happened) in steps)
        {
            Assert.True(Done(chapter, step, f, f, null, happened), $"{chapter}/{step} on {happened}");
            Assert.False(Done(chapter, step, f, f, null, [.. all.Where(e => e != happened)]), $"{chapter}/{step} without {happened}");
        }
    }

    [Fact]
    public void AFloorOrACeilingMovedFinishesItsOwnStepOnly()
    {
        var map = Built(TwoRooms);
        var start = map.Frame;
        var floor = Then(map, "floor 128 128 32");
        Assert.True(Done("rooms", "floor", floor, start));
        Assert.False(Done("rooms", "ceiling", floor, start));
        var ceiling = Then(map, "ceiling 128 128 96");
        Assert.True(Done("rooms", "ceiling", ceiling, floor));
        Assert.False(Done("rooms", "floor", ceiling, floor));
    }

    [Fact]
    public void ALockAKeyAndASwitchFinishTheirStepsAndAnUnlockedDoorDoesNot()
    {
        var freedoom = ProblemTests.Freedoom.Value;
        var map = Built(TwoRooms);
        var door = Then(map, "door 256 128");
        Assert.False(Done("doors", "lock", door, door));
        Assert.False(Done("doors", "switch", door, door));
        var locked = Then(map, "lock 256 128 red");
        Assert.True(Done("doors", "lock", locked, door));
        Assert.False(Done("doors", "key", locked, door, freedoom));
        var key = Then(map, "key red 64 64");
        Assert.True(Done("doors", "key", key, locked, freedoom));
        Assert.False(Done("doors", "switch", key, door));
        var remote = Then(map, "switch 256 128 0 128");
        Assert.True(Done("doors", "switch", remote, key));
    }

    [Fact]
    public void ALiftAndStairsFinishTheirStepsAndFlatRoomsDoNot()
    {
        var map = Built(TwoRooms + "\nfloor 384 128 64");
        var start = map.Frame;
        var lift = Then(map, "lift 384 128");
        Assert.True(Done("lifts", "lift", lift, start));
        Assert.False(Done("lifts", "stairs", lift, start));
        var flat = Then(map, "room 0 256 128 384\nroom 128 256 256 384");
        Assert.False(Done("lifts", "stairs", flat, lift));
        var stairs = Then(map, "stairs 0 64 192 192");
        Assert.True(Done("lifts", "stairs", stairs, flat));
        Assert.False(Done("lifts", "lift", stairs, flat));
    }

    [Fact]
    public void ATextureSlidFinishesTheAlignStepAndARetexturedWallDoesNot()
    {
        var map = Built(TwoRooms);
        var start = map.Frame;
        var side = map.Lines.First(l => l.OnMap).Front;
        Set(map, side, s => s.Middle, (s, v) => s.Middle = v, "BROWN1");
        Assert.False(Done("textures", "align", map.Frame, start));
        Set(map, side, s => s.OffsetX, (s, v) => s.OffsetX = v, 16.0);
        Assert.True(Done("textures", "align", map.Frame, start));
    }

    [Fact]
    public void AMonsterTakenOffEasyFinishesTheSkillStepAndOneMovedDoesNot()
    {
        var map = Built(TwoRooms + "\nthing 3001 64 64");
        var start = map.Frame;
        var imp = map.Things.Single(t => t.Type == 3001);
        Set(map, imp, t => t.X, (t, v) => t.X = v, 96.0);
        Assert.False(Done("things", "skill", map.Frame, start));
        new History(map).Record("Appears on Easy", e => e.Flag(imp, "skill1", false));
        Assert.True(Done("things", "skill", map.Frame, start));
    }

    [Fact]
    public void BrightnessALightAndAColourFinishTheirStepsOnly()
    {
        var map = Built(TwoRooms);
        var start = map.Frame;
        Set(map, map.Sectors[0], s => s.Light, (s, v) => s.Light = v, 96);
        Assert.True(Done("lights", "brightness", map.Frame, start));
        Assert.False(Done("lights", "colour", map.Frame, start));
        Assert.False(Done("lights", "light", map.Frame, start));
        var dim = map.Frame;
        var light = Then(map, "light 128 128");
        Assert.True(Done("lights", "light", light, dim));
        Assert.False(Done("lights", "brightness", light, dim));
        new History(map).Record("Change the light colour", e => e.Field(map.Sectors[1], "lightcolor", Core.Maps.Udmf.UdmfValue.Int(0xFF4040)));
        Assert.True(Done("lights", "colour", map.Frame, light));
    }

    [Fact]
    public void ASecretAndItsHiddenDoorFinishTheirStepsAndAPaintedWallDoesNot()
    {
        var map = Built(TwoRooms + "\nroom 512 96 576 160");
        var start = map.Frame;
        var secret = Then(map, "secret 544 128");
        Assert.True(Done("secrets", "secret", secret, start));
        var door = Then(map, "door 512 128");
        Assert.False(Done("secrets", "hide", door, secret));
        var other = map.Lines.First(l => l.OnMap && l.Back is null && l.Special == 0).Front;
        Set(map, other, s => s.Top, (s, v) => s.Top = v, "BROWN1");
        Assert.False(Done("secrets", "hide", map.Frame, door));
        var painted = map.Frame;
        var face = map.Lines.First(l => l.OnMap && l.Special is Editor.Smart.Doors.DoorRaise && l.Args[0] == 0);
        Set(map, face.Front, s => s.Top, (s, v) => s.Top = v, "STARTAN3");
        Assert.True(Done("secrets", "hide", map.Frame, painted));
    }

    [Fact]
    public void EveryChapterHasStepsAndTheFirstMapKeepsItsSeven()
    {
        Assert.Equal(["room", "look", "door", "texture", "things", "exit", "play"], GuideBook.Chapter(null).Steps.Select(s => s.Id));
        Assert.Equal(["first", "rooms", "doors", "lifts", "textures", "things", "lights", "secrets", "settings", "veterans"], GuideBook.Chapters.Select(c => c.Id));
        foreach (var c in GuideBook.Chapters)
        {
            Assert.NotEmpty(c.Steps);
            Assert.Equal(c.Steps.Count, c.Steps.Select(s => s.Id).Distinct().Count());
            Assert.NotEmpty(c.TryIt);
        }

        // Every step is one these tests finish against its near miss; a new step needs its own test before it is listed here.
        string[] tested =
        [
            "room", "look", "door", "texture", "things", "exit", "play", "floor", "ceiling", "heights", "doorlook", "lock", "key", "switch", "lift", "stairs", "walk",
            "flat", "align", "monster", "pickup", "skill", "difficulty", "brightness", "light", "colour", "secret", "hide", "name", "music", "names", "keys", "palette",
        ];
        Assert.Empty(GuideBook.Chapters.SelectMany(c => c.Steps).Select(s => s.Id).Except(tested));

        Assert.DoesNotContain(GuideBook.Chapter(null).Steps, s => s.Target == GuideTarget.None);
        Assert.Equal(("a Gargoyle", "the Ethereal Crossbow"), GuideBook.Examples("heretic"));
        Assert.Equal(("an Imp", "a shotgun"), GuideBook.Examples("doom"));
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

                guide.Happened(GuideEvents.Played);
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

/// <summary>
/// The Guide's chapters in the real window: opened from the Guide menu and from a finished card, and the steps that wait
/// on the editor (heights, Walk, a difficulty, the map's name and music, Preferences, the palette) finished by the
/// editor's own controls.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class GuideChaptersInTheWindowTests
{
    [Fact]
    public async Task ChaptersRunFromTheMenuAndTheirStepsHearTheEditor()
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
                string Now() => editor.Guide is { } g ? $"{g.StepLabel}: {g.Title}" : "no guide";
                void Settle()
                {
                    var (at, label) = (editor.Guide?.Index, editor.Guide?.StepLabel);
                    for (var i = 0; i < 100 && editor.Guide?.Index == at && editor.Guide?.StepLabel == label; i++) Ui.Pump(0.02);
                    Ui.Pump(0.05);
                }

                void Open(string title)
                {
                    editor.GuideMenu.Single(l => l.Title == title).Open.Execute(null);
                    Ui.Pump(0.3);
                }

                void Skip(int n)
                {
                    for (var i = 0; i < n; i++) editor.Guide!.SkipCommand.Execute(null);
                    Ui.Pump(0.3);
                }

                Open("Rooms and heights");
                Skip(3);
                said.Add(Now());
                editor.IsFloors = true;
                Ui.Pump(0.3);
                editor.ShowHeights = true;
                Settle();
                said.Add(Now());

                Open("Lifts and stairs");
                Skip(2);
                editor.CameraMode = App.ThreeD.CameraMode.Fly;
                Ui.Pump(0.3);
                said.Add(Now());
                editor.CameraMode = App.ThreeD.CameraMode.Walk;
                Settle();
                said.Add(Now());

                Open("Monsters, items and difficulty");
                Skip(3);
                editor.ChooseSkillCommand.Execute("0");
                Settle();
                said.Add(Now());

                Open("Map settings and music");
                said.Add(Now());
                editor.Session.ChangeMapSettings(m => m with { Music = editor.Session.Resources!.Music.First() });
                Ui.Pump(0.3);
                said.Add(Now());
                editor.Session.ChangeMapSettings(m => m with { Title = "My first hall" });
                Settle();
                said.Add(Now());
                editor.Session.ChangeMapSettings(m => m with { Music = editor.Session.Resources!.Music.Last() });
                Settle();
                said.Add(Now());
                Skip(1);
                said.Add($"{Now()} → {string.Join(", ", editor.Guide!.Next.Select(l => l.Title))}");

                editor.Guide!.Next.Single().Open.Execute(null);
                Ui.Pump(0.3);
                said.Add(Now());
                vm.OpenPreferencesCommand.Execute(null);
                vm.Preferences!.ClassicNames = true;
                Settle();
                said.Add(Now());
                vm.Preferences!.DoomBuilderKeys = true;
                Settle();
                vm.Preferences!.CloseCommand.Execute(null);
                said.Add(Now());
                editor.OpenPaletteCommand.Execute(null);
                editor.Palette!.CloseCommand.Execute(null);
                Ui.Pump(0.3);
                said.Add(Now());
                editor.OpenPaletteCommand.Execute(null);
                editor.Palette!.RunCommand.Execute(editor.Commands().Single(c => c.Name == "Show heights as colours"));
                Settle();
                said.Add($"{Now()} → {editor.Guide!.Next.Count} next");
            }
            finally
            {
                App.App.Services!.Settings.Current.ClassicNames = false;
                App.App.Services.Settings.Current.DoomBuilderKeys = false;
                App.App.Services.Words.Classic = false;
                window.Close();
            }

            return said;
        });
        Assert.Equal(
        [
            "Rooms and heights · Step 4 of 4: See every height at once",
            "Rooms and heights: Done.",
            "Lifts and stairs · Step 3 of 3: Walk up them",
            "Lifts and stairs: Done.",
            "Monsters, items and difficulty: Done.",
            "Map settings and music · Step 1 of 3: Name your map",
            "Map settings and music · Step 1 of 3: Name your map",
            "Map settings and music · Step 2 of 3: Choose its music",
            "Map settings and music · Step 3 of 3: Hear it",
            "Map settings and music: Done. → Next: For veterans: classic names and keys",
            "For veterans: classic names and keys · Step 1 of 3: Classic names",
            "For veterans: classic names and keys · Step 2 of 3: Doom Builder keys",
            "For veterans: classic names and keys · Step 3 of 3: Find any command",
            "For veterans: classic names and keys · Step 3 of 3: Find any command",
            "For veterans: classic names and keys: Done. → 0 next",
        ], log);
    }
}
