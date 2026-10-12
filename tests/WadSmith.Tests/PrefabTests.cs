using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Prefabs: rooms and things saved in the project and used again in any of its maps.</summary>
public sealed class PrefabTests
{
    private static readonly Editor.Look Look = Editor.Look.For("doom");

    [Fact]
    public void ARoomSavedAsAPrefabCanBeUsedInAnotherMap()
    {
        using var temp = new TempFolder();
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192));
        var door = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Door", e => Editor.Smart.Doors.Make(e, MapGeometry.Build(map.Frame), door, Look));
        history.Record("Things", e =>
        {
            e.Add(new Thing { X = 128, Y = 128, Type = 3001 });
            e.Add(new Thing { X = 64, Y = 64, Type = 1 });
        });
        var selection = map.Sectors.Where(s => s.OnMap).Cast<Element>().ToList();

        var first = Prefabs.Save(temp.Path, MapGeometry.Build(map.Frame), selection, "zdoom", Look)!;
        var second = Prefabs.Save(temp.Path, MapGeometry.Build(map.Frame), selection, "zdoom", Look)!;
        Assert.Equal(("Prefab 1", "Prefab 2"), (first.Name, second.Name));
        Assert.Equal(["Prefab 1", "Prefab 2"], Prefabs.In(temp.Path).Select(p => p.Name));
        Assert.Null(Prefabs.Save(temp.Path, MapGeometry.Build(map.Frame), [], "zdoom", Look));
        Assert.Equal("Named", Prefabs.Save(temp.Path, MapGeometry.Build(map.Frame), selection, "zdoom", Look, " Na/me:d ")!.Name);

        // Into an empty map, as a paste: the three areas, the door working as one, the imp; not the player start.
        var clip = Prefabs.Load(first);
        var (other, otherHistory, _) = MovingTests.Drawn();
        otherHistory.Record("Use the prefab", e => Copying.Paste(e, MapGeometry.Build(other.Frame), clip, Copying.PlaceNear(clip, new MapPoint(2048, 0)), Look));
        DrawingTests.AssertSound(other);
        Assert.Equal(3, other.Sectors.Count(s => s.OnMap));
        var shapes = MapGeometry.Build(other.Frame);
        Assert.Single(other.Sectors.Where(s => s.OnMap), s => Editor.Smart.Doors.Recognise(shapes, s) is not null);
        Assert.Equal([3001], other.Things.Where(t => t.OnMap).Select(t => t.Type));
        Assert.Equal(0, other.Things.Single(t => t.OnMap).X % 64 - 128 % 64);
    }

    [Fact]
    public void APrefabOfThingsAloneKeepsThem()
    {
        // Two monsters picked on their own: the prefab has no room, only them.
        using var temp = new TempFolder();
        var (map, history, _) = MovingTests.Drawn((0, 0, 512, 512));
        var imps = new[] { new Thing { X = 128, Y = 128, Type = 3001 }, new Thing { X = 256, Y = 128, Type = 3001 } };
        history.Record("Imps", e => imps.ToList().ForEach(t => e.Add(t)));
        var prefab = Prefabs.Save(temp.Path, MapGeometry.Build(map.Frame), imps, "zdoom", Look)!;
        var clip = Prefabs.Load(prefab);
        Assert.Equal("2 things", clip.Describe());
    }
}
