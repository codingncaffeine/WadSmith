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
                var said = new List<string>
                {
                    $"{session.Look.Door} is {picture.Height} tall in a room {room.CeilingHeight - room.FloorHeight} tall",
                    $"frames: {string.Join(", ", frames.Select(f => f.CeilingHeight - f.FloorHeight))}",
                    $"door opens to {found?.OpensTo}, {editor.Inspector.Heading}",
                };

                // A click on the plan lands on the door's face: the Inspector shows the door all the same, the face's details below.
                session.Selection.Set(found!.Own.First());
                Ui.Pump(0.3);
                said.Add($"its face selected: {editor.Inspector.Heading}; {string.Join(", ", editor.Inspector.Rows.Where(r => r.Label is "Locked with" or "Looks like" or "THIS FACE").Select(r => r.Label))}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        var height = seen[0].Split(' ')[2];
        Assert.Equal([seen[0], $"frames: {height}, {height}", $"door opens to {int.Parse(height, System.Globalization.CultureInfo.InvariantCulture) - 4}, Door", "its face selected: Door; Looks like, Locked with, THIS FACE"], seen);
        Assert.True(int.Parse(height, System.Globalization.CultureInfo.InvariantCulture) < 128, seen[0]);
    }

    /// <summary>
    /// The user's report (2026-10-14): a door clicked onto a long wall is 64 wide, and choosing a big door for it showed half the
    /// picture. Picked in the Inspector's strip, BIGDOOR2 makes the doorway 128 wide about its middle and as tall as itself;
    /// DOOR1 brings it back to 64. With WADSMITH_DOOR_SHOT set to a folder, the window is kept there as door-big.png.
    /// </summary>
    [Fact]
    public async Task ABigDoorsPictureWidensADoorOnALongWall()
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
                var corners = session.Map.Vertices.Where(v => v.OnMap).ToList();
                var (x1, y0, y1) = (corners.Max(v => v.X), corners.Min(v => v.Y), corners.Max(v => v.Y));
                session.DrawRoom([new(x1, y0), new(x1 + 256, y0), new(x1 + 256, y1), new(x1, y1)]);
                Ui.Pump(0.5);
                var wall = session.Map.Lines.Single(l => l.OnMap && l.Back is not null);
                session.MakeDoor(wall);
                Ui.Pump(0.8);
                var door = session.Selection.Items.OfType<Sector>().Single();
                Door Now() => Doors.Recognise(session.Geometry, door)!;
                string Width() => string.Join("/", Now().Own.Select(l => l.Length.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Distinct());
                var said = new List<string> { $"made: {Width()} wide, {Now().Own.First().Front.Top}" };

                var strip = editor.Inspector.Rows.OfType<App.ViewModels.PictureChoiceRow>().Single(r => r.Label == "Looks like");
                strip.Choices.Single(c => c.Name == "BIGDOOR2").IsCurrent = true;
                Ui.Pump(0.8);
                var frames = Doors.FramesOf(session.Geometry, Now());
                said.Add($"BIGDOOR2: {Width()} wide, frames {string.Join("/", frames.Select(f => f.Frame.CeilingHeight - f.Frame.FloorHeight).Distinct())} tall, {(editor.Status.StartsWith("The picture is", StringComparison.Ordinal) ? "told why" : "nothing to say")}");
                if (Environment.GetEnvironmentVariable("WADSMITH_DOOR_SHOT") is { Length: > 0 } shots)
                {
                    session.Selection.Clear();
                    Ui.Pump(1.0);
                    using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
                    frame.Save(Path.Combine(shots, "door-big.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    session.Selection.Set(door);
                    Ui.Pump(0.3);
                }

                strip = editor.Inspector.Rows.OfType<App.ViewModels.PictureChoiceRow>().Single(r => r.Label == "Looks like");
                strip.Choices.Single(c => c.Name == "DOOR1").IsCurrent = true;
                Ui.Pump(0.8);
                said.Add($"DOOR1: {Width()} wide");
                return said;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(["made: 64 wide, DOOR3", "BIGDOOR2: 128 wide, frames 128 tall, nothing to say", "DOOR1: 64 wide"], seen);
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
                said.Add($"after DOOR1 by right-click: faces {string.Join("/", found.Own.Select(l => l.Front.Top).Distinct())}, opens to {found.OpensTo} under a {picture.Height} picture, {menu.Count - strip.Choices.Count} choices before the looks, nothing for a plain wall: {editor.ContextMenuFor(session.Map.Lines.First(l => l.OnMap && l.Back is null && ReferenceEquals(l.Front.Sector, room))).Count}");

                // Locking: by the right-click menu, and by letting a key go on the door; a key let go in a room lies there.
                var told = new List<string>();
                session.Notice += s => told.Add(s);
                menu.Single(c => c.Header == "Lock with the red key").Act();
                Ui.Pump(0.5);
                var lockedMenu = editor.ContextMenuFor(door);
                said.Add($"locked by right-click: lock {Doors.Recognise(session.Geometry, door)!.Settings.Lock}, menu marks '{lockedMenu.Single(c => c.Header.Contains("red", StringComparison.Ordinal)).Header}' and offers {(lockedMenu.Any(c => c.Header == "Unlock") ? "Unlock" : "no unlock")}; told: {told.LastOrDefault()?.Split('.')[0]}");
                lockedMenu.Single(c => c.Header == "Unlock").Act();
                Ui.Pump(0.5);
                var b = session.Geometry.WallBoundsAt(door.Slot)!.Value;
                var middle = new WadSmith.Editor.Geometry.MapPoint((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
                var thingsBefore = session.Map.Things.Count(t => t.OnMap);
                session.DropThing(5, middle, middle, 14);
                Ui.Pump(0.5);
                session.DropThing(5, new(-64, -64), new(-64, -64), 14);
                Ui.Pump(0.5);
                said.Add($"after unlock then a blue key dropped on the door and one in the room: lock {Doors.Recognise(session.Geometry, door)!.Settings.Lock}, {session.Map.Things.Count(t => t.OnMap) - thingsBefore} key placed");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        var count = seen[0].Split(' ')[0];
        Assert.Equal(
        [
            $"{count} looks, DOOR3 in use, among them BIGDOOR2 DOOR1 DOOR3",
            "after BIGDOOR2: faces BIGDOOR2, 2 frames as tall as the room, opens to 124",
            "after DOOR1 by right-click: faces DOOR1, opens to 68 under a 72 picture, 4 choices before the looks, nothing for a plain wall: 0",
            "locked by right-click: lock 129, menu marks '✓ Lock with the red key' and offers Unlock; told: Locked with the red key",
            "after unlock then a blue key dropped on the door and one in the room: lock 130, 1 key placed",
        ], seen);
    }
}
