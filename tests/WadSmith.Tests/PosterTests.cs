using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>A picture of the user's own dropped on a wall or a floor: once, whole, in its own shape (the user, 2026-10-13: "i don't want it to repeat on the wall but i need more space than what shift dragging is providing").</summary>
public sealed class PosterTests
{
    private static (EditorSession Session, string Texture) Open(TempFolder temp, int width, int height)
    {
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var picture = Path.Combine(temp.Path, "poster.png");
        File.WriteAllBytes(picture, ImageCodec.EncodePng(new RgbaImage(width, height)));
        var made = Pictures.Import(project, game, picture);
        return (EditorSession.Open(project, "MAP01", new SessionHooks(a => a(), game)), made.Texture);
    }

    private static Line North(EditorSession session) => session.Map.Lines.Where(l => l.OnMap && l.Back is null).MaxBy(l => l.Start.Y + l.End.Y)!;

    private static double Length(Line l) => Math.Sqrt(Math.Pow(l.End.X - l.Start.X, 2) + Math.Pow(l.End.Y - l.Start.Y, 2));

    [Fact]
    public void YourPictureTakesASectionOfItsOwnShapeAndTheRestOfTheWallStays()
    {
        using var temp = new TempFolder();
        var (session, texture) = Open(temp, 100, 100);
        var wall = North(session);
        var (before, length, tall) = (wall.Front.Middle, Length(wall), wall.Front.Sector.CeilingHeight - wall.Front.Sector.FloorHeight);
        var middle = new MapPoint((wall.Start.X + wall.End.X) / 2, (wall.Start.Y + wall.End.Y) / 2 - 4);
        Assert.Null(session.Paint(texture, middle, 8, false));

        // Square: a section as wide as the wall is tall, round where it was let go; the wall either side as it was.
        var pieces = session.Map.Lines.Where(l => l.OnMap && l.Back is null && Math.Abs(l.Start.Y - wall.Start.Y) < 1e-6 && Math.Abs(l.End.Y - wall.Start.Y) < 1e-6).ToList();
        Assert.Equal(3, pieces.Count);
        var poster = pieces.Single(l => l.Front.Middle == texture);
        Assert.Equal(tall, Length(poster), 3);
        Assert.All(pieces.Where(l => l != poster), l => Assert.Equal(before, l.Front.Middle));
        Assert.Equal(length, pieces.Sum(Length), 3);
        // One copy fills it: 100 pixels over tall across and down.
        Assert.True(poster.Front.Extra.TryGet("scalex_mid", out var sx) && Math.Abs(sx.AsFloat() - 100 / tall) < 1e-5);
        Assert.True(poster.Front.Extra.TryGet("scaley_mid", out var sy) && Math.Abs(sy.AsFloat() - 100 / tall) < 1e-5);

        // One undo puts the wall back whole.
        session.History.Undo();
        Assert.Equal(before, North(session).Front.Middle);
        Assert.Equal(length, Length(North(session)), 3);
    }

    [Fact]
    public void APictureWiderThanTheWallTakesTheWholeWall()
    {
        using var temp = new TempFolder();
        var (session, texture) = Open(temp, 4000, 100);
        var wall = North(session);
        var middle = new MapPoint((wall.Start.X + wall.End.X) / 2, (wall.Start.Y + wall.End.Y) / 2 - 4);
        session.Paint(texture, middle, 8, false);
        var after = North(session);
        Assert.Equal(Length(wall), Length(after), 3);
        Assert.Equal(texture, after.Front.Middle);
        Assert.True(after.Front.Extra.Contains("scalex_mid"));
    }

    [Fact]
    public void ShiftStillPutsItOnASectionAsWideAsThePicture()
    {
        using var temp = new TempFolder();
        var (session, texture) = Open(temp, 96, 32);
        var wall = North(session);
        var middle = new MapPoint((wall.Start.X + wall.End.X) / 2, (wall.Start.Y + wall.End.Y) / 2 - 4);
        session.Paint(texture, middle, 8, false, section: true);
        var piece = session.Map.Lines.Single(l => l.OnMap && l.Front.Middle == texture);
        Assert.Equal(96, Length(piece), 3);
        Assert.False(piece.Front.Extra.Contains("scalex_mid"));
    }

    [Fact]
    public void YourPictureOnAFloorFillsTheRoomOnce()
    {
        using var temp = new TempFolder();
        var (session, texture) = Open(temp, 64, 64);
        var room = session.Map.Sectors.First(s => s.OnMap);
        var inside = session.Map.Lines.Where(l => l.OnMap).Select(l => l.Start).ToList();
        var at = new MapPoint(inside.Average(v => v.X), inside.Average(v => v.Y));
        session.Paint(texture, at, 0.1, false);
        Assert.Equal(texture, room.FloorTexture);
        Assert.True(room.Extra.Contains("xscalefloor") || room.Extra.Contains("xpanningfloor"));
    }
}
