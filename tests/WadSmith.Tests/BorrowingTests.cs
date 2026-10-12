using System.Collections.Concurrent;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

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
