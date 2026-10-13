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
}
