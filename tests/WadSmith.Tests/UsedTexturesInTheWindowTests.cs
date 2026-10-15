using Avalonia.Controls;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;

namespace WadSmith.Tests;

/// <summary>
/// The Textures shelf's "Used in this map" (D65, Ultimate Doom Builder's used textures): chosen in the shelf's own list, it
/// shows the pictures on the map's walls, floors and ceilings, and follows the map when a wall gets another picture.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class UsedTexturesInTheWindowTests
{
    [Fact]
    public async Task TheUsedGroupShowsThePicturesTheMapUsesAndFollowsIt()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                editor.ShowTextures = true;
                var shelf = editor.TextureShelf;
                Assert.True(Ui.Until(() => shelf.Tiles.Count > 0, 30), "The Textures shelf did not fill.");

                // What the map uses, read from the map itself.
                string Map() => string.Join(" ", session.Map.Sides.Where(s => s.OnMap).SelectMany(s => new[] { s.Top, s.Middle, s.Bottom })
                    .Concat(session.Map.Sectors.Where(s => s.OnMap).SelectMany(s => new[] { s.FloorTexture, s.CeilingTexture }))
                    .Where(n => n != "-").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));
                string Shown() => string.Join(" ", shelf.Tiles.Select(t => t.Label).Order(StringComparer.Ordinal));

                var groups = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, shelf.Groups));
                groups.SelectedIndex = shelf.Groups.ToList().IndexOf(ShelfViewModel.UsedGroup);
                Ui.Until(() => !shelf.Note.StartsWith("Finding", StringComparison.Ordinal) && shelf.Note.Contains("this map uses", StringComparison.Ordinal), 10);
                var said = new List<string> { $"first: {(Shown() == Map() ? "the map's pictures" : $"{Shown()} (the map uses {Map()})")}, {shelf.Note}" };

                // A wall of the start room gets another picture: the group follows.
                var wall = session.Map.Sides.First(s => s.OnMap);
                const string other = "BIGDOOR2";
                session.History.Record("Paint", e => e.Set(wall, s => s.Middle, (s, v) => s.Middle = v, other));
                Ui.Until(() => shelf.Tiles.Any(t => t.Label == other), 10);
                said.Add($"after a wall got {other}: {(Shown() == Map() ? "the map's pictures" : $"{Shown()} (the map uses {Map()})")}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(2, seen.Count);
        Assert.StartsWith("first: the map's pictures, ", seen[0], StringComparison.Ordinal);
        Assert.Matches(@"^first: the map's pictures, \d+ pictures this map uses\. Drag one onto the map\.$", seen[0]);
        Assert.Equal("after a wall got BIGDOOR2: the map's pictures", seen[1]);
    }
}
