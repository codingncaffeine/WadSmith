using System.Collections.Concurrent;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Pictures from another game the user owns, copied into a project when used (D43).</summary>
public sealed class BorrowingTests
{
    private static readonly Lazy<IReadOnlyList<FoundGame>> Games = new(() => GameLocator.Scan([Path.GetDirectoryName(GameFiles.Require("DOOM2.WAD"))!, Path.GetDirectoryName(GameFiles.Require("HERETIC.WAD"))!]));

    private static GameResources Load(string engine, string? folder = null)
    {
        var id = Games.Value.First(g => g.Game.Engine == engine && g.Game.IsBaseGame && (engine != "doom" || g.Game.Id == "doom2")).Game.Id;
        return GameResources.Load(id, folder, Games.Value, GameFiles.Require("gzdoom.pk3")) ?? throw new InvalidOperationException($"{id} was not loaded");
    }

    private static (TempFolder Temp, Project Project, GameResources Doom, GameResources Heretic) Setup()
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        return (temp, project, Load("doom", project.Folder), Load("heretic"));
    }

    [Fact]
    public void AHereticWallIsCopiedAsItLooksAndIsThereForTheMap()
    {
        var (temp, project, doom, heretic) = Setup();
        using var _ = temp;
        const string name = "GRSKULL1";
        Assert.False(doom.Textures.Has(name));
        var chosen = Borrowing.CopyPicture(project, doom, heretic, name, flat: false);
        Assert.Equal(name, chosen);

        var file = Path.Combine(project.Folder, "textures", "GRSKULL1.png");
        var copied = ImageCodec.Decode(File.ReadAllBytes(file));
        var original = heretic.Textures.GetWall(name)!;
        Assert.Equal((original.Width, original.Height), (copied.Width, copied.Height));
        Assert.Equal(original.Pixels, copied.Pixels);
        Assert.Equal(original.Pixels, doom.Textures.GetWall(name)!.Pixels);

        var record = Assert.Single(Project.Open(project.Folder).Info.Copied);
        Assert.Equal(("wall", heretic.Game.Id, name, name, "textures/GRSKULL1.png"), (record.Kind, record.Game, record.Original, record.Name, record.File));

        // A second use finds the first copy.
        Assert.Equal(name, Borrowing.CopyPicture(project, doom, heretic, name, flat: false));
        Assert.Single(project.Info.Copied);
        Assert.Single(Directory.GetFiles(Path.Combine(project.Folder, "textures")));
    }

    [Fact]
    public void ANameThisGameHasGetsTheOtherGamesLetterAndFloorsGoToFlats()
    {
        var (temp, project, doom, heretic) = Setup();
        using var _ = temp;
        Assert.True(doom.Textures.Has("SKY1") && heretic.Textures.FindWall("SKY1") is not null);
        Assert.Equal("HSKY1", Borrowing.CopyPicture(project, doom, heretic, "SKY1", flat: false));
        Assert.Equal(heretic.Textures.GetWall("SKY1")!.Pixels, doom.Textures.GetWall("HSKY1")!.Pixels);
        Assert.NotEqual(doom.Textures.GetWall("SKY1")!.Pixels, doom.Textures.GetWall("HSKY1")!.Pixels);

        var flat = heretic.Textures.Flats.Select(f => f.Name).First(n => !doom.Textures.Has(n) && n.Length >= 4 && !n.StartsWith("F_SKY", StringComparison.Ordinal));
        Assert.Equal(flat, Borrowing.CopyPicture(project, doom, heretic, flat, flat: true));
        Assert.True(File.Exists(Path.Combine(project.Folder, "flats", flat + ".png")));
        Assert.Equal(heretic.Textures.GetFlat(flat)!.Pixels, doom.Textures.GetFlat(flat)!.Pixels);
    }

    [Fact]
    public void FreeNamesStayWithinEightLetters()
    {
        var (temp, _, doom, heretic) = Setup();
        using var __ = temp;
        Assert.Equal("NEWNAME", Borrowing.FreeName(doom.Textures, "newname", heretic.Game));
        Assert.True(doom.Textures.Has("STARTAN3"));
        Assert.Equal("HSTARTAN", Borrowing.FreeName(doom.Textures, "STARTAN3", heretic.Game));
        Assert.All(new[] { "STARTAN3", "SKY1", "FLOOR4_8" }, n => Assert.InRange(Borrowing.FreeName(doom.Textures, n, heretic.Game).Length, 1, 8));
    }

    private static GameResources LoadId(string id, string? folder = null) => GameResources.Load(id, folder, Games.Value, GameFiles.Require("gzdoom.pk3")) ?? throw new InvalidOperationException($"{id} was not loaded");

    /// <summary>A copy of a starter room of <paramref name="from"/>'s game, its walls and floor painted, with objects of the given numbers.</summary>
    private static MapClip Room(GameResources from, string engine, string wall, string floor, params int[] things)
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", engine));
        var history = new History(map);
        history.Record("Paint", e =>
        {
            foreach (var l in map.Lines) e.Set(l.Front, x => x.Middle, (x, v) => x.Middle = v, wall);
            e.Set(map.Sectors[0], x => x.FloorTexture, (x, v) => x.FloorTexture = v, floor);
            var y = 0;
            foreach (var type in things) e.Add(new Editor.Model.Thing { X = 0, Y = y += 40, Type = type });
        });
        var clip = Copying.Copy(MapGeometry.Build(map.Frame), [map.Sectors[0]]);
        clip.From = from;
        return clip;
    }

    private static List<string> PasteInto(EditorSession session, MapClip clip)
    {
        // The paste runs on a worker, which adds the rooms and then says what it copied: wait for both (the rooms alone
        // were seen once under a busy suite before the sentence came).
        var told = new ConcurrentQueue<string>();
        session.Notice += told.Enqueue;
        var before = session.Map.Sectors.Count(x => x.OnMap);
        session.Paste(clip, new MapPoint(1024, 0));
        var until = DateTime.UtcNow.AddSeconds(15);
        while (session.Map.Sectors.Count(x => x.OnMap) == before && DateTime.UtcNow < until) Thread.Sleep(10);
        var said = DateTime.UtcNow.AddSeconds(2);
        while (told.IsEmpty && DateTime.UtcNow < said) Thread.Sleep(10);
        return [.. told];
    }

    [Fact]
    public void ARoomPastedFromHereticBringsItsPicturesAndLeavesOutWhatDoomCallsSomethingElse()
    {
        var (temp, project, doom, heretic) = Setup();
        using var _ = temp;
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), doom));
        var floor = heretic.Textures.Flats.Select(x => x.Name).First(n => !doom.Textures.Has(n) && !n.StartsWith("F_SKY", StringComparison.Ordinal));

        // 66 is Heretic's Gargoyle and Doom's Revenant; 14 is where a teleporter sends in both.
        Assert.NotEqual(heretic.Things.Find(66)!.ClassName, doom.Things.Find(66)!.ClassName);
        Assert.Equal(heretic.Things.Find(14)!.ClassName, doom.Things.Find(14)!.ClassName);
        var told = PasteInto(session, Room(heretic, "heretic", "GRSKULL1", floor, 66, 14));

        var pasted = session.Map.Sectors.Last(x => x.OnMap);
        Assert.Equal(floor, pasted.FloorTexture);
        Assert.Equal(heretic.Textures.GetFlat(floor)!.Pixels, doom.Textures.GetFlat(floor)!.Pixels);
        Assert.Equal(heretic.Textures.GetWall("GRSKULL1")!.Pixels, doom.Textures.GetWall("GRSKULL1")!.Pixels);
        Assert.Equal([14], session.Map.Things.Where(t => t.OnMap && t.Type != 1).Select(t => t.Type));
        Assert.Contains(told, t => t.Contains("copied into this project from Heretic", StringComparison.Ordinal) && t.Contains("left out", StringComparison.Ordinal));
        Assert.Contains(Project.Open(project.Folder).Info.Copied, c => c.Original == "GRSKULL1");
    }

    [Fact]
    public void APictureThisGameHasItsOwnOfArrivesUnderANewNameAndThePasteUsesIt()
    {
        var (temp, project, doom, heretic) = Setup();
        using var _ = temp;
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), doom));
        PasteInto(session, Room(heretic, "heretic", "SKY1", "FLOOR4_8"));
        var pasted = session.Map.Sectors.Last(x => x.OnMap);
        var walls = session.Map.Lines.Where(l => l.OnMap && ReferenceEquals(l.Front.Sector, pasted)).ToList();
        Assert.NotEmpty(walls);
        Assert.All(walls, l => Assert.Equal("HSKY1", l.Front.Middle));
        Assert.Equal(heretic.Textures.GetWall("SKY1")!.Pixels, doom.Textures.GetWall("HSKY1")!.Pixels);
    }

    [Fact]
    public void CopyingRemembersTheGameItCameFrom()
    {
        var (temp, project, doom, _) = Setup();
        using var __ = temp;
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), doom));
        session.Selection.Set(session.Map.Sectors[0]);
        Assert.True(session.CopySelection());
        Assert.Same(doom, Copying.Clipboard!.From);
        Copying.Clipboard = null;
    }

    [Fact]
    public void APictureBothGamesShowAlikeIsNotCopied()
    {
        var (temp, project, doom, _) = Setup();
        using var __ = temp;
        var ultimate = LoadId(Games.Value.First(g => Path.GetFileName(g.Path).Equals("DOOM.WAD", StringComparison.OrdinalIgnoreCase)).Game.Id);
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), doom));
        Assert.Equal(ultimate.Textures.GetWall("STARTAN3")!.Pixels, doom.Textures.GetWall("STARTAN3")!.Pixels);
        PasteInto(session, Room(ultimate, "doom", "STARTAN3", "FLOOR4_8"));
        Assert.Empty(Project.Open(project.Folder).Info.Copied);
        Assert.Equal("STARTAN3", session.Map.Lines.Last(l => l.OnMap).Front.Middle);
    }

    [Fact]
    public void DroppingAPictureFromAnotherGamePaintsTheWallWithTheCopy()
    {
        var (temp, project, doom, heretic) = Setup();
        using var _ = temp;
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new ConcurrentQueue<Action>();
        using var session = EditorSession.Open(project, "MAP01", new SessionHooks(ui.Enqueue, doom));
        var told = new List<string>();
        session.Notice += told.Add;
        var wall = session.Map.Lines[0];
        var mid = new MapPoint((wall.Start.X + wall.End.X) / 2, (wall.Start.Y + wall.End.Y) / 2);
        session.PaintBorrowed(heretic, "SKY1", false, mid, 8, false);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (told.Count == 0 && DateTime.UtcNow < until)
        {
            while (ui.TryDequeue(out var a)) a();
            Thread.Sleep(10);
        }

        Assert.Equal("HSKY1", wall.Front.Middle);
        Assert.Contains("as HSKY1", Assert.Single(told));
        Assert.Equal("Put a texture on a wall", session.History.UndoLabel);
    }
}
