using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>Kept copies of a map (D24): written, listed newest first, never doubled, and brought back.</summary>
public sealed class VersionTests
{
    private static string Udmf(MapModel m) => Core.Maps.Udmf.UdmfText.WriteString(MapConversion.ToUdmf(m));

    [Fact]
    public void CopiesListNewestFirstAndAnUnchangedMapIsNotKeptTwice()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var map = StarterRoom.Create("MAP01", "doom");
        var t0 = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

        Assert.NotNull(MapVersions.Write(project, "MAP01", map, "Opened", t0));
        Assert.Null(MapVersions.Write(project, "MAP01", map, "Kept by hand", t0.AddMinutes(1)));
        map.Sectors[0].FloorHeight = 32;
        Assert.NotNull(MapVersions.Write(project, "MAP01", map, "Kept by hand", t0.AddMinutes(2)));

        var list = MapVersions.List(project, "MAP01");
        Assert.Equal(["Kept by hand", "Opened"], list.Select(v => v.Reason));
        Assert.All(list, v => Assert.True(File.Exists(v.Picture)));
        Assert.Equal(32, MapVersions.Load(list[0]).Sectors[0].FloorHeight);
        Assert.Equal(0, MapVersions.Load(list[1]).Sectors[0].FloorHeight);
    }

    [Fact]
    public void CopiesKeptBeforeMillisecondsWereNamedStillList()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var written = MapVersions.Write(project, "MAP01", StarterRoom.Create("MAP01", "doom"), "Opened", DateTimeOffset.Now)!;
        var old = Path.Combine(MapVersions.Folder(project, "MAP01"), "20261001-120000.wad");
        File.Copy(written.File, old);

        var list = MapVersions.List(project, "MAP01");
        Assert.Equal(2, list.Count);
        Assert.Equal(old, list[1].File);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0), list[1].When.LocalDateTime);
    }

    [Fact]
    public async Task GoingBackMakesTheCopyTheMapAndSaysSo()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var map = StarterRoom.Create("MAP01", "doom");
        project.SaveMap(map);
        var old = MapVersions.Write(project, "MAP01", map, "Opened", DateTimeOffset.Now.AddMinutes(-5))!;
        map.Sectors[0].FloorHeight = 48;
        project.SaveMap(map);

        // As the editor does: the map as it is now is kept first.
        Assert.NotNull(MapVersions.Write(project, "MAP01", map, "Before going back", DateTimeOffset.Now.AddMinutes(-1)));

        using var session = EditorSession.Restore(project, "MAP01", old, new SessionHooks(_ => { }));
        await session.OpeningVersion;

        Assert.Equal(0, session.Map.Sectors[0].FloorHeight);
        Assert.Equal(Udmf(MapVersions.Load(old)), Udmf(Project.Open(project.Folder).LoadMap("MAP01")));
        var list = MapVersions.List(project, "MAP01");
        Assert.StartsWith("Back to the version from ", list[0].Reason, StringComparison.Ordinal);
        Assert.Equal(["Before going back", "Opened"], list.Skip(1).Select(v => v.Reason));
    }

    [Fact]
    public void WhenAVersionWasKeptReadsLikeACalendar()
    {
        var now = new DateTime(2026, 10, 11, 21, 0, 0);
        Assert.StartsWith("Today ", VersionItem.Describe(new DateTime(2026, 10, 11, 8, 5, 0), now), StringComparison.Ordinal);
        Assert.StartsWith("Yesterday ", VersionItem.Describe(new DateTime(2026, 10, 10, 23, 0, 0), now), StringComparison.Ordinal);
        Assert.DoesNotContain("2026", VersionItem.Describe(new DateTime(2026, 9, 3, 14, 0, 0), now), StringComparison.Ordinal);
        Assert.Contains("2025", VersionItem.Describe(new DateTime(2025, 9, 3, 14, 0, 0), now), StringComparison.Ordinal);
    }
}
