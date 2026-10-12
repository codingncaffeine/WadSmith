using WadSmith.Core.Maps;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>The user's maps, listed by game, renamed, copied and deleted (to a Deleted folder, never gone).</summary>
public sealed class ProjectListTests
{
    private static Project Make(string projects, string folder, string name, string game)
    {
        var p = Project.Create(Path.Combine(projects, folder), name, game);
        p.SaveMap(new MapModel { Name = "MAP01", Namespace = "zdoom" }, origin: new MapOrigin { Kind = "new" });
        return p;
    }

    [Fact]
    public void MapsAreListedNewestFirstRenamedCopiedAndDeletedIntoTheDeletedFolder()
    {
        using var temp = new TempFolder();
        var projects = Path.Combine(temp.Path, "projects");
        Make(projects, "Cave", "Cave", "doom2");
        Thread.Sleep(30);
        Make(projects, "Tower", "Tower", "heretic");
        Thread.Sleep(30);
        var fort = Make(projects, "Fort", "Fort", "doom2");
        Directory.CreateDirectory(Path.Combine(projects, "not a project"));

        var listed = Project.List(projects);
        Assert.Equal(["Fort", "Tower", "Cave"], listed.Select(p => p.Info.Name));
        Assert.Equal(["doom2", "heretic", "doom2"], listed.Select(p => p.Info.Game));

        // Renamed: the sidecar says so, the folder stays.
        fort.Rename("Fortress");
        Assert.Equal("Fortress", Project.Open(fort.Folder).Info.Name);
        Assert.True(Directory.Exists(Path.Combine(projects, "Fort")));
        Assert.Throws<ArgumentException>(() => fort.Rename("  "));

        // Copied: everything in it, under the new name.
        var copy = Project.Copy(fort.Folder, Path.Combine(projects, "Fortress copy"), "Fortress copy");
        Assert.Equal("Fortress copy", copy.Info.Name);
        Assert.True(File.Exists(Path.Combine(copy.Folder, "maps", "MAP01.wad")));
        Assert.Equal(4, Project.List(projects).Count);
        Assert.Throws<IOException>(() => Project.Copy(fort.Folder, copy.Folder, "again"));

        // Deleted: moved into Deleted, out of the list, and whole.
        var where = Project.Delete(copy.Folder, projects);
        Assert.StartsWith(Path.Combine(projects, Project.DeletedFolderName), where);
        Assert.True(File.Exists(Path.Combine(where, "maps", "MAP01.wad")));
        Assert.False(Directory.Exists(copy.Folder));
        Assert.Equal(["Fortress", "Tower", "Cave"], Project.List(projects).Select(p => p.Info.Name));
    }
}
