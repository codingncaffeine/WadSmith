using System.Collections.Concurrent;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A straight flight cut again into another number of steps, one undo step.</summary>
public sealed class ReStepTests
{
    private sealed class Open : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly ConcurrentQueue<Action> _ui = new();

        public Open()
        {
            var project = Project.Create(Path.Combine(_temp.Path, "p"), "p", "doom2");
            project.SaveMap(StarterRoom.Create("MAP01", "doom"));
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

    /// <summary>
    /// A room east of the starter room raised to 64, and stairs from one to the other along x, wider (256) than long (128),
    /// so a re-cut must keep the way they climb rather than follow the longer side (3 steps of 16).
    /// </summary>
    private static Staircase Flight(Open o)
    {
        var s = o.Session;
        s.DrawRoom([new(384, -256), new(384, 256), new(640, 256), new(640, -256)]);
        o.Settle();
        var high = s.Map.Sectors.Last(x => x.OnMap);
        s.History.Record("High", e => e.Set(high, x => x.FloorHeight, (x, v) => x.FloorHeight = v, 64.0));
        o.Settle();
        var g = s.Geometry;
        IReadOnlyList<Sector> steps = [];
        s.History.Record("Stairs", e => steps = Stairs.Make(e, g, new MapPoint(256, -128), new MapPoint(384, 128), s.Look, alongX: true).Steps);
        o.Settle();
        return StairsFinder.Recognise(s.Geometry, steps[0])!;
    }

    [Fact]
    public void ThreeStepsBecomeFiveEvenlyAndOneUndoBringsThemBack()
    {
        using var o = new Open();
        var flight = Flight(o);
        Assert.Equal(3, flight.Count);
        var before = o.Session.Map.Sectors.Count(x => x.OnMap);
        o.Session.ReStep(flight.Steps[0], 5);
        o.Settle();

        var now = StairsFinder.Recognise(o.Session.Geometry, o.Session.Selection.Items.OfType<Sector>().First())!;
        Assert.Equal(5, now.Count);
        Assert.Equal([11.0, 21.0, 32.0, 43.0, 53.0], now.Heights);
        Assert.Equal((0.0, 64.0), (now.Below, now.Above));
        Assert.Equal(before + 2, o.Session.Map.Sectors.Count(x => x.OnMap));
        DrawingTests.AssertSound(o.Session.Map);

        Assert.True(o.Session.History.Undo());
        o.Settle();
        Assert.Equal(before, o.Session.Map.Sectors.Count(x => x.OnMap));
        Assert.Equal(3, StairsFinder.Recognise(o.Session.Geometry, flight.Steps[0])!.Count);
        Assert.Equal("Stairs", o.Session.History.UndoLabel);
    }

    [Fact]
    public void TooFewStepsToClimbOrTooManyToStandOnAreRefusedLeavingTheFlightAsItWas()
    {
        using var o = new Open();
        var flight = Flight(o);
        var label = o.Session.History.UndoLabel;
        o.Session.ReStep(flight.Steps[0], 1);
        o.Settle();
        Assert.Contains("more than the player can climb", o.Told[^1]);
        o.Session.ReStep(flight.Steps[0], 12);
        o.Settle();
        Assert.Contains("under 16 deep", o.Told[^1]);
        Assert.Equal(label, o.Session.History.UndoLabel);
        Assert.Equal(3, StairsFinder.Recognise(o.Session.Geometry, flight.Steps[0])!.Count);
    }

    [Fact]
    public void AFlightOfAnotherShapeIsNotCutAgain()
    {
        // Three rooms in an L, each a step: no single rectangle.
        using var o = new Open();
        var s = o.Session;
        foreach (var box in new[] { (256.0, -256.0, 384.0, -128.0), (384.0, -256.0, 512.0, -128.0), (384.0, -128.0, 512.0, 0.0), (384.0, 0.0, 512.0, 128.0) })
        {
            s.DrawRoom([new(box.Item1, box.Item2), new(box.Item1, box.Item4), new(box.Item3, box.Item4), new(box.Item3, box.Item2)]);
            o.Settle();
        }

        var rooms = s.Map.Sectors.Where(x => x.OnMap).Skip(1).ToList();
        s.History.Record("Heights", e =>
        {
            for (var i = 0; i < rooms.Count; i++)
            {
                var k = i;
                e.Set(rooms[k], x => x.FloorHeight, (x, v) => x.FloorHeight = v, 16.0 * (k + 1));
            }
        });
        o.Settle();
        var flight = StairsFinder.Recognise(s.Geometry, rooms[1])!;
        Assert.Null(StairsFinder.Run(s.Geometry, flight));
        s.ReStep(rooms[1], 4);
        o.Settle();
        Assert.Contains("fill one rectangle", o.Told[^1]);
    }
}
