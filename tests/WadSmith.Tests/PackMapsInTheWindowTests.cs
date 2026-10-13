using Avalonia.Controls;
using Avalonia.VisualTree;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>A map pack beside the games is listed on the tile of the game it is for, and one click opens its map with the pack loaded (D52).</summary>
[Collection(TimingCollection.Name)]
public sealed class PackMapsInTheWindowTests
{
    [Fact]
    public async Task APackBesideTheGameIsOnItsTileAndOpensWithThePack()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var games = Directory.CreateDirectory(Path.Combine(temp.Path, "games")).FullName;
        File.Copy(wad, Path.Combine(games, "freedoom2.wad"));
        // Editing a map from a pack reads the engine's rules: its data file goes beside the games, as CI has no GZDoom installed.
        File.Copy(GameFiles.Require("gzdoom.pk3"), Path.Combine(games, "gzdoom.pk3"));
        var model = StarterRoom.Create("MAP03", "doom");
        var text = System.Text.Encoding.UTF8.GetBytes(UdmfText.WriteString(MapConversion.ToUdmf(model)));
        var pack = Path.Combine(games, "shreds.wad");
        File.WriteAllBytes(pack, WadWriter.Write(WadKind.Pwad, [("MAP03", ReadOnlyMemory<byte>.Empty), ("TEXTMAP", text), ("ENDMAP", ReadOnlyMemory<byte>.Empty)]));
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), games);
            try
            {
                var said = new List<string>();
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == "freedoom2");
                var choice = tile.PackMaps.Single(p => p.Pack.Name == "shreds.wad");
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content is string c && c.StartsWith("Maps found near your games", StringComparison.Ordinal) && ReferenceEquals(b.DataContext, tile));
                said.Add($"{button.Content}: {choice.Label}");

                Ui.Wait(tile.EditPackMap!.ExecuteAsync(choice));
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                var session = editor.Session;
                said.Add($"opened {session.Project.Info.Name} ({session.Slot}): {session.Map.Sectors.Count(s => s.OnMap)} room, add-on {string.Join(", ", session.Project.Info.AddOns.Select(Path.GetFileName))}, game {session.Resources?.Game.Id}, files {string.Join(" + ", session.Resources?.Files.Select(Path.GetFileName) ?? [])}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["Maps found near your games · 1 ▾: shreds.wad › MAP03", "opened shreds MAP03 (MAP03): 1 room, add-on shreds.wad, game freedoom2, files freedoom2.wad + shreds.wad"], seen);
    }
}
