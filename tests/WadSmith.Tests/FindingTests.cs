using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Find and replace (D58): each kind finds what it should and only that, and Replace all changes only the matching parts, as one step.</summary>
public sealed class FindingTests
{
    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    [Fact]
    public void WallPicturesAreFoundByNameOrPatternAndReplacedOnlyWhereTheyMatch()
    {
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var solid = map.Lines.First(l => l.OnMap && l.Back is null);
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Dress", e =>
        {
            foreach (var l in map.Lines.Where(l => l.OnMap && l.Back is null)) e.Set(l.Front, s => s.Middle, (s, v) => s.Middle = v, "BROWN1");
            e.Set(solid.Front, s => s.Middle, (s, v) => s.Middle = v, "PIPE2");
            e.Set(shared.Front, s => s.Bottom, (s, v) => s.Bottom = v, "PIPE4");
            e.Set(shared.Front, s => s.Top, (s, v) => s.Top = v, "BROWN1");
        });

        var exact = Finding.Find(Shapes(map), new FindQuery(FindKind.WallPicture, "pipe2"), null);
        Assert.Equal([(Element)solid.Front], exact.Select(f => f.Element));
        var pattern = Finding.Find(Shapes(map), new FindQuery(FindKind.WallPicture, "PIPE*"), null);
        Assert.Equal(["middle", "lower"], pattern.Select(f => f.Where));

        // Replacing PIPE? changes the solid wall's middle and the opening's lower, not the opening's upper BROWN1.
        var count = 0;
        history.Record("Replace", e => count = Finding.Replace(e, new FindQuery(FindKind.WallPicture, "PIPE?"), pattern, "GRAY1", null));
        Assert.Equal(2, count);
        Assert.Equal(("GRAY1", "GRAY1", "BROWN1"), (solid.Front.Middle, shared.Front.Bottom, shared.Front.Top));
        Assert.Empty(Finding.Find(Shapes(map), new FindQuery(FindKind.WallPicture, "PIPE*"), null));
        history.Undo();
        Assert.Equal(("PIPE2", "PIPE4"), (solid.Front.Middle, shared.Front.Bottom));
    }

    [Fact]
    public void FloorPicturesSayWhichPartMatched()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (512, 0, 640, 128));
        history.Record("Dress", e =>
        {
            e.Set(rooms[0], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1");
            e.Set(rooms[1], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NUKAGE1");
            e.Set(rooms[1], s => s.CeilingTexture, (s, v) => s.CeilingTexture = v, "NUKAGE1");
        });
        var found = Finding.Find(Shapes(map), new FindQuery(FindKind.FloorPicture, "NUKAGE1"), null);
        Assert.Equal(["floor", "floor and ceiling"], found.Select(f => f.Where));
        history.Record("Replace", e => Finding.Replace(e, new FindQuery(FindKind.FloorPicture, "NUKAGE1"), found, "FWATER1", null));
        Assert.Equal(("FWATER1", "FWATER1", "FWATER1"), (rooms[0].FloorTexture, rooms[1].FloorTexture, rooms[1].CeilingTexture));
        Assert.NotEqual("FWATER1", rooms[0].CeilingTexture);
    }

    [Fact]
    public void ObjectsAreFoundByNumberOrNameAndByDifficulty()
    {
        var things = ProblemTests.Freedoom.Value.Things;
        var (map, history, _) = MovingTests.Drawn((0, 0, 512, 512));
        Thing Placed(int type, int x, params string[] skills)
        {
            var t = new Thing { X = x, Y = 64, Type = type };
            foreach (var s in skills) t.SetFlag(s, true);
            history.Record("Place", e => e.Add(t));
            return t;
        }

        var easyImp = Placed(3001, 64, "skill1", "skill2");
        var hardImp = Placed(3001, 128, "skill4", "skill5");
        var demon = Placed(3002, 192, "skill1", "skill2", "skill3", "skill4", "skill5");
        var g = Shapes(map);
        var impName = things.Find(3001)!.Name;

        Assert.Equal([easyImp, hardImp], Finding.Find(g, new FindQuery(FindKind.Object, "3001"), things).Select(f => f.Element));
        Assert.Equal([easyImp, hardImp], Finding.Find(g, new FindQuery(FindKind.Object, impName[..3].ToLowerInvariant()), things).Select(f => f.Element).Where(e => e != demon));
        Assert.Equal([hardImp], Finding.Find(g, new FindQuery(FindKind.Object, "3001", FindSkills.Hard), things).Select(f => f.Element));

        // Replaced by name: every imp becomes the demon's kind; the demon is untouched.
        var found = Finding.Find(g, new FindQuery(FindKind.Object, "3001"), things);
        Assert.Null(Finding.WhyNotReplace(new FindQuery(FindKind.Object, "3001"), things.Find(3002)!.Name, things, null));
        history.Record("Replace", e => Finding.Replace(e, new FindQuery(FindKind.Object, "3001"), found, things.Find(3002)!.Name, things));
        Assert.Equal([3002, 3002, 3002], new[] { easyImp, hardImp, demon }.Select(t => t.Type));
        Assert.Contains("not one object", Finding.WhyNotReplace(new FindQuery(FindKind.Object, "3001"), "o", things, null));
    }

    [Fact]
    public void ActionsEffectsAndTagsAreFoundAndRenumbered()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (512, 0, 640, 128));
        var wall = map.Lines.First(l => l.OnMap && l.Front.Sector == rooms[0]);
        var door = Editor.Smart.Doors.DoorOpen;
        history.Record("Wire", e =>
        {
            e.Set(rooms[1], s => s.Tag, (s, v) => s.Tag = v, 7);
            e.Set(rooms[1], s => s.Special, (s, v) => s.Special = v, 1024 | 71);
            e.Set(wall, l => l.Special, (l, v) => l.Special = v, door);
            e.Set(wall, l => l.Args[0], (l, v) => l.Args[0] = v, 7);
        });
        var g = Shapes(map);

        Assert.Equal([(Element)wall], Finding.Find(g, new FindQuery(FindKind.WallAction, door.ToString(System.Globalization.CultureInfo.InvariantCulture)), null).Select(f => f.Element));
        Assert.Equal([(Element)rooms[1]], Finding.Find(g, new FindQuery(FindKind.RoomEffect, "nukage"), null).Select(f => f.Element));
        var tagged = Finding.Find(g, new FindQuery(FindKind.Tag, "7"), null);
        Assert.Equal([(Element)rooms[1], wall], tagged.Select(f => f.Element));

        // A new tag for both keeps the switch working on its room; a new effect keeps the secret flag.
        history.Record("Retag", e => Finding.Replace(e, new FindQuery(FindKind.Tag, "7"), tagged, "12", null));
        Assert.Equal((12, 12), (rooms[1].Tag, wall.Args[0]));
        var effect = Finding.Find(Shapes(map), new FindQuery(FindKind.RoomEffect, "71"), null);
        history.Record("Effect", e => Finding.Replace(e, new FindQuery(FindKind.RoomEffect, "71"), effect, "Heals", null));
        Assert.Equal(1024 | 196, rooms[1].Special);
    }

    [Fact]
    public void ASearchThatCannotRunSaysWhy()
    {
        Assert.Equal("Type what to look for.", Finding.WhyNot(new FindQuery(FindKind.WallPicture, " "), null));
        Assert.Equal("A tag is a number above 0.", Finding.WhyNot(new FindQuery(FindKind.Tag, "red"), null));
        Assert.Contains("No room effect", Finding.WhyNot(new FindQuery(FindKind.RoomEffect, "zzz"), null));
        Assert.Contains("one name", Finding.WhyNotReplace(new FindQuery(FindKind.WallPicture, "A*"), "B*", null, null));
        Assert.Contains("no picture called", Finding.WhyNotReplace(new FindQuery(FindKind.WallPicture, "A"), "NOPE", null, _ => false));
    }
}
