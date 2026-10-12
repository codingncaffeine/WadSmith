using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>Several rooms' heights set or raised together, and a room levelled with one beside it (§3.4).</summary>
public sealed class HeightsTests
{
    private static (EditorSession Session, TempFolder Temp) Open(MapModel m)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(m);
        return (EditorSession.Open(project, "MAP01", _ => { }), temp);
    }

    [Fact]
    public void TwoRoomsShowTheirRangeTakeOneFloorAndRiseTogether()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        var (west, east) = (session.Map.Sectors[0], session.Map.Sectors[1]);
        session.Selection.Set(west, east);
        var floors = inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Floors at");
        Assert.Equal(0, floors.Value);
        Assert.Equal("they differ: 0 to 32", floors.HintText);

        floors.Value = 16;
        Assert.Equal((16.0, 16.0), (west.FloorHeight, east.FloorHeight));
        Assert.Equal("Change floor heights", session.History.UndoLabel);

        inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Raise them all by").Value = 8;
        Assert.Equal((24.0, 24.0, 136.0, 104.0), (west.FloorHeight, east.FloorHeight, west.CeilingHeight, east.CeilingHeight));
        Assert.Equal(0, inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Raise them all by").Value);

        session.History.Undo();
        session.History.Undo();
        Assert.Equal((0.0, 32.0, 128.0, 96.0), (west.FloorHeight, east.FloorHeight, west.CeilingHeight, east.CeilingHeight));
    }

    [Fact]
    public void ARoomIsLevelledWithTheFloorOrCeilingOfTheRoomBeside()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        var west = session.Map.Sectors[0];
        session.Selection.Set(west);
        var floor = inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Floor level with");
        Assert.Equal(["a room beside…", "32 (the room beside)"], floor.Options);
        floor.Value = 1;
        Assert.Equal(32, west.FloorHeight);
        inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Ceiling level with").Value = 1;
        Assert.Equal(96, west.CeilingHeight);
        Assert.Equal("Match the ceiling", session.History.UndoLabel);
    }
}
