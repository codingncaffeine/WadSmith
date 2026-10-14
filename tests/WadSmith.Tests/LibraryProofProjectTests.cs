using WadSmith.Core.Games;
using WadSmith.Core.Library;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>
/// Writes a project holding free community objects downloaded from Realm667 for real (D61) — a monster in Doom's style, one in
/// Heretic's and Hexen's (drawn in Heretic's colours) and a prop — each brought in as dragging it onto the map does and placed in
/// the room, for the engine's own check (tools/engine/play-check.sh WORK FOLDER GAMES). It reaches the internet, so it is
/// skipped unless WADSMITH_LIBRARY_PROOF names a folder that does not exist yet; what was brought is written beside it.
/// </summary>
public sealed class LibraryProofProjectTests
{
    [Fact]
    public async Task WritesAProjectWithDownloadedObjectsPlacedForGZDoom()
    {
        var folder = Environment.GetEnvironmentVariable("WADSMITH_LIBRARY_PROOF");
        if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) Assert.Skip("Set WADSMITH_LIBRARY_PROOF to a new folder to write the downloaded objects' proof project.");
        var cancel = TestContext.Current.CancellationToken;
        var wad = GameFiles.Require("DOOM2.WAD");
        var engine = GameFiles.Require("gzdoom.pk3");
        var game = GameCatalog.Get("doom2")!;
        var project = Project.Create(folder!, "Library proof", "doom2");
        var model = StarterRoom.Create("MAP01", "doom");
        project.SaveMap(model);
        var resources = GameResources.Load("doom2", project.Folder, [new FoundGame(game, wad, new FileInfo(wad).Length)], engine)!;

        // Another family's colours from its game here, as the app finds them.
        Core.Graphics.Palette? PaletteOf(string family) => family switch
        {
            "heretic" => GameFiles.Find("HERETIC.WAD") is { } h && Core.Containers.ResourceContainer.Open(h).Find("PLAYPAL") is { } p ? new Core.Graphics.Palette(p.Read()) : null,
            _ => null,
        };

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WadSmith/0.1.0 (+https://github.com/codingncaffeine/WadSmith)");
        var store = new LibraryStore(folder + "-store", http);
        var said = new List<string>();
        var x = -160.0;
        foreach (var path in new[] { "beastiary/doom-style", "beastiary/heretic-hexen-style", "prop-stop/technical" })
        {
            var category = Realm667.Categories.Single(c => c.Path == path);
            var items = await store.Realm667Async(category, cancel);
            var item = items.First();
            var files = await store.FetchAsync(item, null, cancel);
            var brought = LibraryObjects.Bring(project, resources, item, files, PaletteOf);
            var thing = new MapThing { X = x, Y = 96, Angle = 270, Type = brought.Main.Number };
            foreach (var f in new[] { "skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm" }) thing.SetFlag(f, true);
            model.Things.Add(thing);
            x += 160;
            said.Add($"{path}: {item.Title} ({item.Id}) as {brought.Main.Thing.ClassName}, number {brought.Main.Number}; {string.Join(" ", brought.Notes)}");
        }

        project.SaveMap(model);
        ProjectMapInfo.Refresh(Project.Open(project.Folder), game, resources.Files, engine);
        File.WriteAllLines(folder + "-brought.txt", said);
        Assert.Equal(3, said.Count);
    }
}
