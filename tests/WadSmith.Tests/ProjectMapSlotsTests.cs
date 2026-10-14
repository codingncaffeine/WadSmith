using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>A project's maps added, copied, moved and deleted by slot (6c): files, list order and the exits between them.</summary>
public sealed class ProjectMapSlotsTests
{
    [Fact]
    public void AMapIsCopiedMovedAndDeletedWithTheExitsThatLedToIt()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"), "First");
        Assert.Equal(["MAP02", "MAP03"], project.FreeSlots().Take(2));

        project.CopyMap("MAP01", "MAP02");
        project.UpdateMap("MAP01", e => e with { Next = "MAP02" });
        Assert.Equal("First", project.Map("MAP02")!.Title);
        Assert.Equal("MAP02", project.LoadMap("MAP02").Name);

        // Moved: the file, its place in the list, and the exit that led to it.
        project.MoveMap("MAP02", "MAP07");
        Assert.Equal(["MAP01", "MAP07"], project.Maps().Select(m => m.Slot));
        Assert.Equal("MAP07", project.Map("MAP01")!.Next);
        Assert.False(File.Exists(Path.Combine(project.Folder, "maps", "MAP02.wad")));
        Assert.Equal("MAP07", project.LoadMap("MAP07").Name);
        var deleted = Path.Combine(project.Folder, ".wadsmith", "deleted");
        Assert.Single(Directory.GetFiles(deleted));
        Assert.Equal(["MAP01", "MAP07"], Project.Open(project.Folder).Maps().Select(m => m.Slot));

        // Deleted: out of the list, the exit forgotten, the file kept; the last map stays.
        project.DeleteMap("MAP07");
        Assert.Equal(["MAP01"], project.Maps().Select(m => m.Slot));
        Assert.Null(project.Map("MAP01")!.Next);
        Assert.Equal(2, Directory.GetFiles(deleted).Length);
        Assert.Throws<InvalidOperationException>(() => project.DeleteMap("MAP01"));
        Assert.Throws<InvalidOperationException>(() => project.CopyMap("MAP01", "MAP01"));
    }

    [Fact]
    public void AGameOfEpisodesFillsItsFirstEpisodeFirst()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "freedoom1");
        project.SaveMap(StarterRoom.Create("E1M1", "doom"));
        Assert.Equal(["E1M2", "E1M3"], project.FreeSlots().Take(2));
        Assert.Equal(81 - 1, project.FreeSlots().Count);
    }
}
