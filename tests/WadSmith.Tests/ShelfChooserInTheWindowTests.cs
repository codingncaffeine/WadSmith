using Avalonia.Controls;
using Avalonia.VisualTree;

namespace WadSmith.Tests;

/// <summary>The shelves' game chooser in the real window (D55): it shows the map's own game, lists the others, and switches the tiles.</summary>
[Collection(TimingCollection.Name)]
public sealed class ShelfChooserInTheWindowTests
{
    /// <summary>The texture shelf of a new map of <paramref name="game"/>, from the games in <paramref name="folder"/>, as the window shows it.</summary>
    private static async Task<List<string>> Choose(string folder, string game, string? other)
    {
        using var temp = new TempFolder();
        return await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, folder);
            try
            {
                var tile = vm.Welcome.Games.Single(g => g.Found.Game.Id == game);
                Ui.Wait(tile.NewMap!.ExecuteAsync(tile));
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                editor.ShowTextures = true;
                var shelf = editor.TextureShelf;
                for (var i = 0; i < 400 && (shelf.Tiles.Count == 0 || (other is not null && !shelf.Games.Any(g => g.StartsWith(other, StringComparison.Ordinal)))); i++) Ui.Pump(0.05);
                Ui.Pump(0.3);

                var chooser = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, shelf.Games));
                var said = new List<string> { $"shows '{chooser.SelectedItem}'; tiles from {string.Join(",", shelf.Tiles.Select(t => t.Game ?? "own").Distinct())}" };
                if (other is not null)
                {
                    chooser.SelectedIndex = shelf.Games.ToList().FindIndex(g => g.StartsWith(other, StringComparison.Ordinal));
                    Ui.Pump(0.3);
                    said.Add($"after choosing: shows '{chooser.SelectedItem}'; tiles from {string.Join(",", shelf.Tiles.Select(t => t.Game ?? "own").Distinct())}, {shelf.Tiles.Count > 0}");
                }

                return said;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task TheChooserShowsThisMapsOwnGame()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        Assert.Equal(["shows 'Freedoom: Phase 2'; tiles from own"], await Choose(Path.GetDirectoryName(wad)!, "freedoom2", null));
    }

    [Fact]
    public async Task ChoosingHereticShowsHereticsPictures()
    {
        var doom2 = GameFiles.Require("DOOM2.WAD");
        GameFiles.Require("HERETIC.WAD");
        Assert.Equal(["shows 'Doom II'; tiles from own", "after choosing: shows 'Heretic'; tiles from Heretic, True"], await Choose(Path.GetDirectoryName(doom2)!, "doom2", "Heretic"));
    }

    [Fact]
    public async Task ChoosingQuakeShowsQuakesPictures()
    {
        var doom2 = GameFiles.Require("DOOM2.WAD");
        GameFiles.Require("PAK0.PAK");
        Assert.Equal(["shows 'Doom II'; tiles from own", "after choosing: shows 'Quake'; tiles from Quake, True"], await Choose(Path.GetDirectoryName(doom2)!, "doom2", "Quake"));
    }
}
