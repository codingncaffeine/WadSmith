using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Where two rooms touch they open into each other: that line is named an opening, offers the door and the window that go
/// in it, and a shape drawn along walls already there is answered in plain words (a first map, 2026-10-12: "it's just
/// extending that room as opposed to putting a wall between them").
/// </summary>
public sealed class OpeningTests
{
    private sealed class Open : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly ConcurrentQueue<Action> _ui = new();

        public Open(MapModel model)
        {
            var project = Project.Create(Path.Combine(_temp.Path, "p"), "p", "doom2");
            project.SaveMap(model);
            Session = EditorSession.Open(project, "MAP01", _ui.Enqueue);
            Session.Notice += Told.Add;
        }

        public EditorSession Session { get; }

        public List<string> Told { get; } = [];

        public void Settle()
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            do
            {
                while (_ui.TryDequeue(out var a)) a();
                Thread.Sleep(5);
            }
            while ((Session.Geometry.Version != Session.Map.Version || !_ui.IsEmpty) && DateTime.UtcNow < until);
        }

        public void Dispose()
        {
            Session.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void TheLineWhereTwoRoomsMeetIsAnOpeningWithADoorAndAWindowOnOffer()
    {
        using var o = new Open(SceneTests.TwoRooms(0, 128));
        o.Settle();
        var shared = Assert.Single(o.Session.Map.Lines, l => l.OnMap && l.Back is not null);
        var inspector = new InspectorViewModel(o.Session);
        o.Session.Selection.Set(shared);
        Assert.Equal("Opening between two rooms", inspector.Heading);
        Assert.Equal(["Make it a door", "Make it a window", "Wall it off"], inspector.Rows.OfType<ActionRow>().Select(r => r.Label).Take(3));
        var explained = Explaining.Explain(shared, o.Session.Geometry, o.Session.Resources)!;
        Assert.Equal("Opening between two rooms", explained.Heading);
        Assert.Contains("open into each other", explained.Text);

        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it a door").Command.Execute(null);
        o.Settle();
        Assert.Equal("Make a door", o.Session.History.UndoLabel);
        Assert.NotNull(Doors.Recognise(o.Session.Geometry, o.Session.Selection.Items.OfType<Sector>().Single()));
        // The opening is 128 wide: a 64 door in its middle and wall either side, so five areas now.
        Assert.Equal(5, o.Session.Map.Sectors.Count(s => s.OnMap));
        Assert.Empty(o.Told);
    }

    [Fact]
    public void AShapeDrawnAlongTheOpeningIsAnsweredInPlainWords()
    {
        using var o = new Open(SceneTests.TwoRooms(0, 128));
        o.Settle();
        var before = o.Session.Map.Lines.Count(l => l.OnMap);

        // "Drawing a wall" between the rooms: corners along the line where they meet, a shape with no room in it.
        o.Session.DrawRoom([new MapPoint(128, 0), new MapPoint(128, 64), new MapPoint(128, 128)]);
        o.Settle();
        Assert.Equal(before, o.Session.Map.Lines.Count(l => l.OnMap));
        var said = Assert.Single(o.Told);
        Assert.Contains("open into each other", said);
        Assert.DoesNotContain("Parameter", said);
        Assert.Contains("pick Door", said);
    }
}
