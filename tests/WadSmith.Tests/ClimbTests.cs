using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>A room joining two floors too far apart (D27): the Inspector offers steps or a ramp, and either one climbs.</summary>
public sealed class ClimbTests
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    /// <summary>A low room (floor 0), a corridor <paramref name="length"/> long drawn against it, and a high room (floor <paramref name="high"/>) past it.</summary>
    private static MapModel Corridor(double length, double high)
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 128 + length, 128), (128 + length, 0, 256 + length, 128));
        history.Record("Raise", e =>
        {
            e.Set(rooms[2], s => s.FloorHeight, (s, v) => s.FloorHeight = v, high);
            e.Set(rooms[2], s => s.CeilingHeight, (s, v) => s.CeilingHeight = v, high + 128);
        });
        return map.ToModel();
    }

    private static (EditorSession Session, TempFolder Temp) Open(MapModel m)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(m);
        return (EditorSession.Open(project, "MAP01", _ => { }), temp);
    }

    /// <summary>The floor under the corridor's middle line every 8 units, from the low room into the high one.</summary>
    private static double[] Floors(EditorSession session, double length)
    {
        var g = MapGeometry.Build(session.Map.Frame);
        return [.. Enumerable.Range(0, (int)(length / 8) + 5).Select(i => 112 + 8.0 * i).Select(x =>
        {
            var p = new MapPoint(x + 0.5, 64);
            return SlopePlane.FloorAt(g.AreaAt(p)!.Value, p);
        })];
    }

    private static string Offered(InspectorViewModel inspector) => string.Join(", ", inspector.Rows.Where(r => r is ActionRow { Label: "Add steps" or "Make a ramp" } || r is InfoRow { Label: "Joins floors" }).Select(r => r is InfoRow i ? i.Value : r.Label));

    [Fact]
    public void OnlyARoomBetweenFloorsMoreThanAStepApartIsAClimb()
    {
        var g = MapGeometry.Build(EditorMap.FromModel(Corridor(128, 64)).Frame);
        var corridor = g.SectorAt(new MapPoint(192, 64))!;
        var climb = Climbs.Find(g, corridor)!;
        Assert.Equal((0.0, 64.0, 128.0, 64.0), (climb.Low.FloorHeight, climb.High.FloorHeight, climb.Length, climb.Rise));
        Assert.Null(Climbs.Find(g, g.SectorAt(new MapPoint(64, 64))!));

        // A step the player climbs is no climb.
        var low = MapGeometry.Build(EditorMap.FromModel(Corridor(128, 24)).Frame);
        Assert.Null(Climbs.Find(low, low.SectorAt(new MapPoint(192, 64))!));
    }

    [Fact]
    public void AddStepsCutsTheCorridorIntoStairsTheyClimbAndOneUndoTakesThemBack()
    {
        var (session, temp) = Open(Corridor(128, 64));
        using var _ = temp;
        using var __ = session;
        var before = Udmf(session.Map.ToModel());
        var inspector = new InspectorViewModel(session);
        var corridor = session.Geometry.SectorAt(new MapPoint(192, 64))!;
        session.Selection.Set(corridor);
        Assert.Equal("0 and 64, 64 apart, Add steps, Make a ramp", Offered(inspector));

        // The offer comes before the room's numbers, where it is seen without scrolling.
        var labels = inspector.Rows.Select(r => r.Label).ToList();
        Assert.True(labels.IndexOf("Make a ramp") < labels.IndexOf("Floor height"), string.Join(" | ", labels));

        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add steps").Command.Execute(null);
        Assert.Equal("Add steps", session.History.UndoLabel);
        DrawingTests.AssertSound(session.Map);
        var floors = Floors(session, 128);
        Assert.Equal(0, floors[0]);
        Assert.Equal(64, floors[^1]);
        Assert.All(floors.Zip(floors.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, Editor.Smart.Stairs.MaxRise));
        Assert.Equal([0.0, 16.0, 32.0, 48.0, 64.0], floors.Distinct());

        session.History.Undo();
        Assert.Equal(before, Udmf(session.Map.ToModel()));
    }

    [Fact]
    public void MakeARampSlopesTheFloorFromTheLowRoomToTheHighOne()
    {
        var (session, temp) = Open(Corridor(128, 64));
        using var _ = temp;
        using var __ = session;
        var before = Udmf(session.Map.ToModel());
        var inspector = new InspectorViewModel(session);
        var corridor = session.Geometry.SectorAt(new MapPoint(192, 64))!;
        session.Selection.Set(corridor);
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make a ramp").Command.Execute(null);
        Assert.Equal("Make a ramp", session.History.UndoLabel);

        var plane = SlopePlane.Floor(corridor)!.Value;
        Assert.Equal((0.0, 32.0, 64.0), (Math.Round(plane.Z(128, 64), 6), Math.Round(plane.Z(192, 10), 6), Math.Round(plane.Z(256, 120), 6)));
        Assert.Equal(64 + Editor.Smart.Stairs.Headroom, corridor.CeilingHeight);
        var floors = Floors(session, 128);
        Assert.All(floors.Zip(floors.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, 8));

        // A ramp is no longer a ledge: nothing is offered.
        Assert.Equal("", Offered(inspector));
        session.History.Undo();
        Assert.Equal(before, Udmf(session.Map.ToModel()));
    }

    [Fact]
    public void AShortCorridorIsTooSteepForARampButTakesSteps()
    {
        var (session, temp) = Open(Corridor(48, 64));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(session.Geometry.SectorAt(new MapPoint(150, 64))!);
        Assert.Equal("0 and 64, 64 apart, Add steps", Offered(inspector));
        Assert.Contains("steeper than the player walks up", Climbs.Ramp(new StructureEdit(session.Map, "x"), session.Geometry, Climbs.Find(session.Geometry, session.Geometry.SectorAt(new MapPoint(150, 64))!)!));

        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add steps").Command.Execute(null);
        var floors = Floors(session, 48);
        Assert.Equal((0.0, 64.0), (floors[0], floors[^1]));
        Assert.All(floors.Zip(floors.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, Editor.Smart.Stairs.MaxRise));
    }
}
