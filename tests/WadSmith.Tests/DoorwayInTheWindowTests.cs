using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>The Door tool in the real editor, with the game's textures: the doorway is as tall as the game's own door picture.</summary>
[Collection(TimingCollection.Name)]
public sealed class DoorwayInTheWindowTests
{
    [Fact]
    public async Task TheDoorToolBuildsADoorwayAsTallAsTheGamesDoorPicture()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var picture = session.Resources!.Textures.FindWall(session.Look.Door)!;
                var room = session.Map.Sectors.Single(s => s.OnMap);
                var x1 = session.Map.Vertices.Where(v => v.OnMap).Max(v => v.X);

                // A corridor 64 wide east of the first room, then the Door tool on the opening between them.
                session.DrawRoom([new(x1, -32), new(x1 + 256, -32), new(x1 + 256, 32), new(x1, 32)]);
                Ui.Pump(0.5);
                var opening = session.Map.Lines.Single(l => l.OnMap && l.Back is not null);
                session.MakeDoor(opening);
                Ui.Pump(0.8);
                var door = session.Selection.Items.OfType<Sector>().Single();
                var frames = session.Map.Sectors.Where(s => s.OnMap && !ReferenceEquals(s, room) && !ReferenceEquals(s, door) && s.CeilingHeight > s.FloorHeight && s.CeilingHeight - s.FloorHeight < room.CeilingHeight - room.FloorHeight).ToList();
                var found = Doors.Recognise(session.Geometry, door);
                return new List<string>
                {
                    $"{session.Look.Door} is {picture.Height} tall in a room {room.CeilingHeight - room.FloorHeight} tall",
                    $"frames: {string.Join(", ", frames.Select(f => f.CeilingHeight - f.FloorHeight))}",
                    $"door opens to {found?.OpensTo}, {editor.Inspector.Heading}",
                };
            }
            finally
            {
                window.Close();
            }
        });
        var height = seen[0].Split(' ')[2];
        Assert.Equal([seen[0], $"frames: {height}, {height}", $"door opens to {int.Parse(height, System.Globalization.CultureInfo.InvariantCulture) - 4}, Door"], seen);
        Assert.True(int.Parse(height, System.Globalization.CultureInfo.InvariantCulture) < 128, seen[0]);
    }

    [Fact]
    public async Task TheDoorsLookIsChosenFromTheGamesOwnDoorsInTheInspectorOrByRightClick()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var room = session.Map.Sectors.Single(s => s.OnMap);
                var x1 = session.Map.Vertices.Where(v => v.OnMap).Max(v => v.X);
                session.DrawRoom([new(x1, -32), new(x1 + 256, -32), new(x1 + 256, 32), new(x1, 32)]);
                Ui.Pump(0.5);
                session.MakeDoor(session.Map.Lines.Single(l => l.OnMap && l.Back is not null));
                Ui.Pump(0.8);
                var door = session.Selection.Items.OfType<Sector>().Single();
                var said = new List<string>();

                // The Inspector's strip: the game's own doors, the one in use marked; picking a tall one opens the doorway up.
                var strip = editor.Inspector.Rows.OfType<App.ViewModels.PictureChoiceRow>().Single(r => r.Label == "Looks like");
                var current = strip.Choices.Single(c => c.IsCurrent).Name;
                var big = strip.Choices.Single(c => c.Name == "BIGDOOR2");
                said.Add($"{strip.Choices.Count} looks, {current} in use, among them {string.Join(" ", strip.Choices.Select(c => c.Name).Where(n => n is "DOOR1" or "DOOR3" or "BIGDOOR2").Order(StringComparer.Ordinal))}");
                big.IsCurrent = true;
                Ui.Pump(0.5);
                var found = Doors.Recognise(session.Geometry, door)!;
                var frames = Doors.FramesOf(session.Geometry, found).Count(f => f.Frame.CeilingHeight - f.Frame.FloorHeight == 128);
                said.Add($"after BIGDOOR2: faces {string.Join("/", found.Own.Select(l => l.Front.Top).Distinct())}, {frames} frames as tall as the room, opens to {found.OpensTo}");

                // The right-click menu offers the same, and takes effect.
                var menu = editor.ContextMenuFor(door);
                menu.Single(c => c.Header == "Looks like DOOR1").Act();
                Ui.Pump(0.5);
                found = Doors.Recognise(session.Geometry, door)!;
                var picture = session.Resources!.Textures.FindWall("DOOR1")!;
                said.Add($"after DOOR1 by right-click: faces {string.Join("/", found.Own.Select(l => l.Front.Top).Distinct())}, opens to {found.OpensTo} under a {picture.Height} picture, {menu.Count} choices, nothing for a plain wall: {editor.ContextMenuFor(session.Map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, room))).Count}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        var count = seen[0].Split(' ')[0];
        Assert.Equal([$"{count} looks, DOOR3 in use, among them BIGDOOR2 DOOR1 DOOR3", "after BIGDOOR2: faces BIGDOOR2, 2 frames as tall as the room, opens to 124", $"after DOOR1 by right-click: faces DOOR1, opens to 68 under a 72 picture, {count} choices, nothing for a plain wall: 0"], seen);
    }
}
