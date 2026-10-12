using System.Collections.Concurrent;
using WadSmith.Core.Checks;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

public sealed class SessionTests
{
    /// <summary>Stands in for the UI dispatcher: queued actions run when the test pumps.</summary>
    private sealed class FakeUi
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action a) => _queue.Enqueue(a);

        public void PumpUntil(Func<bool> done, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (!done())
            {
                while (_queue.TryDequeue(out var a)) a();
                if (DateTime.UtcNow > until) throw new TimeoutException();
                Thread.Sleep(10);
            }
        }
    }

    private static SetProperty<Sector, double> Floor(Sector s, double h) => new(s, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, h);

    [Theory]
    [InlineData("doom")]
    [InlineData("heretic")]
    [InlineData("hexen")]
    [InlineData("strife")]
    public void TheStarterRoomIsSoundAndHasAPlayerStart(string engine)
    {
        var m = StarterRoom.Create("MAP01", engine);
        Assert.Empty(MapValidator.Check(m));
        Assert.Contains(m.Things, t => t.Type == 1);
        var map = EditorMap.FromModel(m);
        var geometry = Editor.Geometry.MapGeometry.Build(map.Frame);
        // The player start stands inside the room, so the room's walls face inward.
        Assert.Same(map.Sectors[0], geometry.SectorAt(new Editor.Geometry.MapPoint(map.Things[0].X, map.Things[0].Y)));
    }

    [Fact]
    public void AChangeIsKeptWithoutAskingAndClosingFlushes()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new FakeUi();
        using var session = EditorSession.Open(project, "MAP01", ui.Post);
        session.History.Run(Floor(session.Map.Sectors[0], 24));
        Assert.Equal(SaveState.Pending, session.State);

        // After the save delay, with nobody asking, the change is on disk.
        ui.PumpUntil(() => session.State == SaveState.Saved, TimeSpan.FromSeconds(10));
        Assert.Equal(24, Project.Open(project.Folder).LoadMap("MAP01").Sectors[0].FloorHeight);

        // A change still inside the delay reaches the disk when the session is flushed.
        session.History.Run(Floor(session.Map.Sectors[0], 48));
        var flush = session.FlushAsync();
        ui.PumpUntil(() => flush.IsCompleted, TimeSpan.FromSeconds(10));
        Assert.Equal(48, Project.Open(project.Folder).LoadMap("MAP01").Sectors[0].FloorHeight);
    }

    [Fact]
    public void OpeningAMapKeepsAVersionWithAPicture()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", _ => { });
        var folder = Path.Combine(project.Folder, ".wadsmith", "versions", "MAP01");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!Directory.Exists(folder) || Directory.GetFiles(folder, "*.png").Length == 0) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.Single(Directory.GetFiles(folder, "*.wad"));
        Assert.Single(Directory.GetFiles(folder, "*.png"));
    }
}
